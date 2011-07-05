#region Copyright & License Information
/*
 * Copyright 2007-2011 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made 
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA.Mods.Cnc.Widgets.Logic
{
	public class SimpleTooltipLogic
	{
		[ObjectCreator.UseCtor]
		public SimpleTooltipLogic([ObjectCreator.Param] Widget widget,
		                          [ObjectCreator.Param] Func<string> getText)
		{
			var label = widget.GetWidget<LabelWidget>("LABEL");
			var cachedWidth = 0;
			var font = Game.Renderer.Fonts[label.Font];
			label.GetText = () =>
			{
				var text = getText();
				var textWidth = font.Measure(text).X;
				if (textWidth != cachedWidth)
				{
					label.Bounds.Width = textWidth;
					widget.Bounds.Width = 2*label.Bounds.X + textWidth;
				}
				return text;
			};
		}
	}
}

