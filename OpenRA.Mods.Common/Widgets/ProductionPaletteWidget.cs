#region Copyright & License Information
/*
 * Copyright 2007-2018 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Lint;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Network;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets
{
	public class ProductionIcon
	{
		public ActorInfo Actor;
		public string Name;
		public HotkeyReference Hotkey;
		public Sprite Sprite;
		public PaletteReference Palette;
		public PaletteReference IconClockPalette;
		public PaletteReference IconDarkenPalette;
		public float2 Pos;
		public List<ProductionItem> Queued;
		public ProductionQueue ProductionQueue;
	}

	public class ProductionPaletteWidget : Widget
	{
		public enum ReadyTextStyleOptions { Solid, AlternatingColor, Blinking }
		public readonly ReadyTextStyleOptions ReadyTextStyle = ReadyTextStyleOptions.AlternatingColor;
		public readonly Color ReadyTextAltColor = Color.Gold;
		public readonly int Columns = 3;
		public readonly int2 IconSize = new int2(64, 48);
		public readonly int2 IconMargin = int2.Zero;
		public readonly int2 IconSpriteOffset = int2.Zero;

		public readonly string TabClick = null;
		public readonly string DisabledTabClick = null;
		public readonly string TooltipContainer;
		public readonly string TooltipTemplate = "PRODUCTION_TOOLTIP";

		// Note: LinterHotkeyNames assumes that these are disabled by default
		public readonly string HotkeyPrefix = null;
		public readonly int HotkeyCount = 0;

		public readonly string ClockAnimation = "clock";
		public readonly string ClockSequence = "idle";
		public readonly string ClockPalette = "chrome";

		public readonly string NotBuildableAnimation = "clock";
		public readonly string NotBuildableSequence = "idle";
		public readonly string NotBuildablePalette = "chrome";

		public readonly bool DrawTime = true;

		[Translate] public readonly string ReadyText = "";
		[Translate] public readonly string HoldText = "";

		public int DisplayedIconCount { get; private set; }
		public int TotalIconCount { get; private set; }
		public event Action<int, int> OnIconCountChanged = (a, b) => { };

		public ProductionIcon TooltipIcon { get; private set; }
		public Func<ProductionIcon> GetTooltipIcon;
		public readonly World World;
		readonly ModData modData;
		readonly OrderManager orderManager;

		public int MinimumRows = 4;
		public int MaximumRows = int.MaxValue;

		public int IconRowOffset = 0;
		public int MaxIconRowOffset = int.MaxValue;

		Lazy<TooltipContainerWidget> tooltipContainer;
		ProductionQueue currentQueue;
		HotkeyReference[] hotkeys;

		public ProductionQueue CurrentQueue
		{
			get { return currentQueue; }
			set { currentQueue = value; RefreshIcons(); }
		}

		public override Rectangle EventBounds { get { return eventBounds; } }
		Dictionary<Rectangle, ProductionIcon> icons = new Dictionary<Rectangle, ProductionIcon>();
		Animation cantBuild, clock;
		Rectangle eventBounds = Rectangle.Empty;

		readonly WorldRenderer worldRenderer;

		SpriteFont overlayFont;
		float2 holdOffset, readyOffset, timeOffset, queuedOffset;

		[CustomLintableHotkeyNames]
		public static IEnumerable<string> LinterHotkeyNames(MiniYamlNode widgetNode, Action<string> emitError, Action<string> emitWarning)
		{
			var prefix = "";
			var prefixNode = widgetNode.Value.Nodes.FirstOrDefault(n => n.Key == "HotkeyPrefix");
			if (prefixNode != null)
				prefix = prefixNode.Value.Value;

			var count = 0;
			var countNode = widgetNode.Value.Nodes.FirstOrDefault(n => n.Key == "HotkeyCount");
			if (countNode != null)
				count = FieldLoader.GetValue<int>("HotkeyCount", countNode.Value.Value);

			if (count == 0)
				return new string[0];

			if (string.IsNullOrEmpty(prefix))
				emitError("{0} must define HotkeyPrefix if HotkeyCount > 0.".F(widgetNode.Location));

			return Exts.MakeArray(count, i => prefix + (i + 1).ToString("D2"));
		}

		[ObjectCreator.UseCtor]
		public ProductionPaletteWidget(ModData modData, OrderManager orderManager, World world, WorldRenderer worldRenderer)
		{
			this.modData = modData;
			this.orderManager = orderManager;
			World = world;
			this.worldRenderer = worldRenderer;
			GetTooltipIcon = () => TooltipIcon;
			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));

			cantBuild = new Animation(world, NotBuildableAnimation);
			cantBuild.PlayFetchIndex(NotBuildableSequence, () => 0);
			clock = new Animation(world, ClockAnimation);
		}

		public override void Initialize(WidgetArgs args)
		{
			base.Initialize(args);

			hotkeys = Exts.MakeArray(HotkeyCount,
				i => modData.Hotkeys[HotkeyPrefix + (i + 1).ToString("D2")]);
		}

		public void ScrollDown()
		{
			if (CanScrollDown)
				IconRowOffset++;
		}

		public bool CanScrollDown
		{
			get
			{
				var totalRows = (TotalIconCount + Columns - 1) / Columns;

				return IconRowOffset < totalRows - MaxIconRowOffset;
			}
		}

		public void ScrollUp()
		{
			if (CanScrollUp)
				IconRowOffset--;
		}

		public bool CanScrollUp
		{
			get { return IconRowOffset > 0; }
		}

		public void ScrollToTop()
		{
			IconRowOffset = 0;
		}

		public IEnumerable<ActorInfo> AllBuildables
		{
			get
			{
				if (CurrentQueue == null)
					return Enumerable.Empty<ActorInfo>();

				return CurrentQueue.AllItems().OrderBy(a => a.TraitInfo<BuildableInfo>().BuildPaletteOrder);
			}
		}

		public override void Tick()
		{
			TotalIconCount = AllBuildables.Count();

			if (CurrentQueue != null && !CurrentQueue.Actor.IsInWorld)
				CurrentQueue = null;

			if (CurrentQueue != null)
				RefreshIcons();
		}

		public override void MouseEntered()
		{
			if (TooltipContainer != null)
				tooltipContainer.Value.SetTooltip(TooltipTemplate,
					new WidgetArgs() { { "player", World.LocalPlayer }, { "getTooltipIcon", GetTooltipIcon } });
		}

		public override void MouseExited()
		{
			if (TooltipContainer != null)
				tooltipContainer.Value.RemoveTooltip();
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			var icon = icons.Where(i => i.Key.Contains(mi.Location))
				.Select(i => i.Value).FirstOrDefault();

			if (mi.Event == MouseInputEvent.Move)
				TooltipIcon = icon;

			if (icon == null)
				return false;

			// Eat mouse-up events
			if (mi.Event != MouseInputEvent.Down)
				return true;

			return HandleEvent(icon, mi.Button, mi.Modifiers);
		}

		protected bool PickUpCompletedBuildingIcon(ProductionIcon icon, ProductionItem item)
		{
			var actor = World.Map.Rules.Actors[icon.Name];

			if (item != null && item.Done && actor.HasTraitInfo<BuildingInfo>())
			{
				World.OrderGenerator = new PlaceBuildingOrderGenerator(CurrentQueue, icon.Name, worldRenderer);
				return true;
			}

			return false;
		}

		public void PickUpCompletedBuilding()
		{
			foreach (var icon in icons.Values)
			{
				var item = icon.Queued.FirstOrDefault();
				if (PickUpCompletedBuildingIcon(icon, item))
					break;
			}
		}

		bool HandleLeftClick(ProductionItem item, ProductionIcon icon, int handleCount)
		{
			if (PickUpCompletedBuildingIcon(icon, item))
			{
				Game.Sound.Play(SoundType.UI, TabClick);
				return true;
			}

			if (item != null && item.Paused)
			{
				// Resume a paused item
				Game.Sound.Play(SoundType.UI, TabClick);
				World.IssueOrder(Order.PauseProduction(CurrentQueue.Actor, icon.Name, false));
				return true;
			}

			if (CurrentQueue.BuildableItems().Any(a => a.Name == icon.Name))
			{
				// Queue a new item
				Game.Sound.Play(SoundType.UI, TabClick);
				Game.Sound.PlayNotification(World.Map.Rules, World.LocalPlayer, "Speech", CurrentQueue.Info.QueuedAudio, World.LocalPlayer.Faction.InternalName);
				World.IssueOrder(Order.StartProduction(CurrentQueue.Actor, icon.Name, handleCount));
				return true;
			}

			return false;
		}

		bool HandleRightClick(ProductionItem item, ProductionIcon icon, int handleCount)
		{
			if (item == null)
				return false;

			Game.Sound.Play(SoundType.UI, TabClick);

			if (item.Paused || item.Done || item.TotalCost == item.RemainingCost)
			{
				// Instant cancel of things we have not started yet and things that are finished
				Game.Sound.PlayNotification(World.Map.Rules, World.LocalPlayer, "Speech", CurrentQueue.Info.CancelledAudio, World.LocalPlayer.Faction.InternalName);
				World.IssueOrder(Order.CancelProduction(CurrentQueue.Actor, icon.Name, handleCount));
			}
			else
			{
				// Pause an existing item
				Game.Sound.PlayNotification(World.Map.Rules, World.LocalPlayer, "Speech", CurrentQueue.Info.OnHoldAudio, World.LocalPlayer.Faction.InternalName);
				World.IssueOrder(Order.PauseProduction(CurrentQueue.Actor, icon.Name, true));
			}

			return true;
		}

		bool HandleMiddleClick(ProductionItem item, ProductionIcon icon, int handleCount)
		{
			if (item == null)
				return false;

			// Directly cancel, skipping "on-hold"
			Game.Sound.Play(SoundType.UI, TabClick);
			Game.Sound.PlayNotification(World.Map.Rules, World.LocalPlayer, "Speech", CurrentQueue.Info.CancelledAudio, World.LocalPlayer.Faction.InternalName);
			World.IssueOrder(Order.CancelProduction(CurrentQueue.Actor, icon.Name, handleCount));

			return true;
		}

		bool HandleEvent(ProductionIcon icon, MouseButton btn, Modifiers modifiers)
		{
			var startCount = modifiers.HasModifier(Modifiers.Shift) ? 5 : 1;
			var cancelCount = modifiers.HasModifier(Modifiers.Ctrl) ? CurrentQueue.QueueLength : startCount;
			var item = icon.Queued.FirstOrDefault();
			var handled = btn == MouseButton.Left ? HandleLeftClick(item, icon, startCount)
				: btn == MouseButton.Right ? HandleRightClick(item, icon, cancelCount)
				: btn == MouseButton.Middle ? HandleMiddleClick(item, icon, cancelCount)
				: false;

			if (!handled)
				Game.Sound.Play(SoundType.UI, DisabledTabClick);

			return true;
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (e.Event == KeyInputEvent.Up || CurrentQueue == null)
				return false;

			var batchModifiers = e.Modifiers.HasModifier(Modifiers.Shift) ? Modifiers.Shift : Modifiers.None;

			// HACK: enable production if the shift key is pressed
			e.Modifiers &= ~Modifiers.Shift;
			var toBuild = icons.Values.FirstOrDefault(i => i.Hotkey != null && i.Hotkey.IsActivatedBy(e));
			return toBuild != null ? HandleEvent(toBuild, MouseButton.Left, batchModifiers) : false;
		}

		public void RefreshIcons()
		{
			icons = new Dictionary<Rectangle, ProductionIcon>();
			var producer = CurrentQueue != null ? CurrentQueue.MostLikelyProducer() : default(TraitPair<Production>);
			if (CurrentQueue == null || producer.Trait == null)
			{
				if (DisplayedIconCount != 0)
				{
					OnIconCountChanged(DisplayedIconCount, 0);
					DisplayedIconCount = 0;
				}

				return;
			}

			var oldIconCount = DisplayedIconCount;
			DisplayedIconCount = 0;

			var rb = RenderBounds;
			var faction = producer.Trait.Faction;

			foreach (var item in AllBuildables.Skip(IconRowOffset * Columns).Take(MaxIconRowOffset * Columns))
			{
				var x = DisplayedIconCount % Columns;
				var y = DisplayedIconCount / Columns;
				var rect = new Rectangle(rb.X + x * (IconSize.X + IconMargin.X), rb.Y + y * (IconSize.Y + IconMargin.Y), IconSize.X, IconSize.Y);

				var rsi = item.TraitInfo<RenderSpritesInfo>();
				var icon = new Animation(World, rsi.GetImage(item, World.Map.Rules.Sequences, faction));
				var bi = item.TraitInfo<BuildableInfo>();
				icon.Play(bi.Icon);

				var pi = new ProductionIcon()
				{
					Actor = item,
					Name = item.Name,
					Hotkey = DisplayedIconCount < HotkeyCount ? hotkeys[DisplayedIconCount] : null,
					Sprite = icon.Image,
					Palette = worldRenderer.Palette(bi.IconPalette),
					IconClockPalette = worldRenderer.Palette(ClockPalette),
					IconDarkenPalette = worldRenderer.Palette(NotBuildablePalette),
					Pos = new float2(rect.Location),
					Queued = currentQueue.AllQueued().Where(a => a.Item == item.Name).ToList(),
					ProductionQueue = currentQueue
				};

				icons.Add(rect, pi);
				DisplayedIconCount++;
			}

			eventBounds = icons.Any() ? icons.Keys.Aggregate(Rectangle.Union) : Rectangle.Empty;

			if (oldIconCount != DisplayedIconCount)
				OnIconCountChanged(oldIconCount, DisplayedIconCount);
		}

		public override void Draw()
		{
			var iconOffset = 0.5f * IconSize.ToFloat2() + IconSpriteOffset;

			overlayFont = Game.Renderer.Fonts["TinyBold"];
			timeOffset = iconOffset - overlayFont.Measure(WidgetUtils.FormatTime(0, World.Timestep)) / 2;
			queuedOffset = new float2(4, 2);
			holdOffset = iconOffset - overlayFont.Measure(HoldText) / 2;
			readyOffset = iconOffset - overlayFont.Measure(ReadyText) / 2;

			if (CurrentQueue == null)
				return;

			var buildableItems = CurrentQueue.BuildableItems();

			var pios = currentQueue.Actor.Owner.PlayerActor.TraitsImplementing<IProductionIconOverlay>();

			// Icons
			foreach (var icon in icons.Values)
			{
				WidgetUtils.DrawSHPCentered(icon.Sprite, icon.Pos + iconOffset, icon.Palette);

				// Draw the ProductionIconOverlay's sprite
				var pio = pios.FirstOrDefault(p => p.IsOverlayActive(icon.Actor));
				if (pio != null)
					WidgetUtils.DrawSHPCentered(pio.Sprite, icon.Pos + iconOffset + pio.Offset(IconSize), worldRenderer.Palette(pio.Palette), 1f);

				// Build progress
				if (icon.Queued.Count > 0)
				{
					var first = icon.Queued[0];
					clock.PlayFetchIndex(ClockSequence,
						() => (first.TotalTime - first.RemainingTime)
							* (clock.CurrentSequence.Length - 1) / first.TotalTime);
					clock.Tick();

					WidgetUtils.DrawSHPCentered(clock.Image, icon.Pos + iconOffset, icon.IconClockPalette);
				}
				else if (!buildableItems.Any(a => a.Name == icon.Name))
					WidgetUtils.DrawSHPCentered(cantBuild.Image, icon.Pos + iconOffset, icon.IconDarkenPalette);
			}

			// Overlays
			foreach (var icon in icons.Values)
			{
				var total = icon.Queued.Count;
				if (total > 0)
				{
					var first = icon.Queued[0];
					var waiting = first != CurrentQueue.CurrentItem() && !first.Done;
					if (first.Done)
					{
						if (ReadyTextStyle == ReadyTextStyleOptions.Solid || orderManager.LocalFrameNumber * worldRenderer.World.Timestep / 360 % 2 == 0)
							overlayFont.DrawTextWithContrast(ReadyText, icon.Pos + readyOffset, Color.White, Color.Black, 1);
						else if (ReadyTextStyle == ReadyTextStyleOptions.AlternatingColor)
							overlayFont.DrawTextWithContrast(ReadyText, icon.Pos + readyOffset, ReadyTextAltColor, Color.Black, 1);
					}
					else if (first.Paused)
						overlayFont.DrawTextWithContrast(HoldText,
							icon.Pos + holdOffset,
							Color.White, Color.Black, 1);
					else if (!waiting && DrawTime)
						overlayFont.DrawTextWithContrast(WidgetUtils.FormatTime(first.RemainingTimeActual, World.Timestep),
							icon.Pos + timeOffset,
							Color.White, Color.Black, 1);

					if (total > 1 || waiting)
						overlayFont.DrawTextWithContrast(total.ToString(),
							icon.Pos + queuedOffset,
							Color.White, Color.Black, 1);
				}
			}
		}

		public override string GetCursor(int2 pos)
		{
			var icon = icons.Where(i => i.Key.Contains(pos))
				.Select(i => i.Value).FirstOrDefault();

			return icon != null ? base.GetCursor(pos) : null;
		}
	}
}
