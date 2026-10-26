using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Emoji.Wpf;

namespace SymbolsTool.Services;

internal static class SymbolGlyphCache
{
	private static readonly Dictionary<string, DrawingImage> Cache = new Dictionary<string, DrawingImage>(StringComparer.Ordinal);

	public static DrawingImage Get(string text)
	{
		if (Cache.TryGetValue(text, out DrawingImage value))
		{
			return value;
		}
		DrawingImage drawingImage = new DrawingImage();
		Image.SetSource(drawingImage, text);
		FixNegativeBounds(drawingImage);
		if (drawingImage.CanFreeze)
		{
			drawingImage.Freeze();
		}
		Cache[text] = drawingImage;
		return drawingImage;
	}

	public static bool IsCached(string text)
	{
		return Cache.ContainsKey(text);
	}

	/// <summary>
	/// Emoji.Wpf 对某些 Unicode 扩展符号（麻将、扑克等 U+1F0xx）
	/// 生成的 DrawingGroup 里几何路径带有负的 X/Y 坐标偏移，
	/// 但整体 Bounds 被一个透明背景矩形撑到 (0,0,w,h)，
	/// 导致 WPF Image 显示时负坐标区域被挤出可视范围 → 看起来是空白方框。
	/// 此方法把所有 Drawing 里的几何路径平移到正坐标区域。
	/// </summary>
	private static void FixNegativeBounds(DrawingImage img)
	{
		var root = img.Drawing;
		if (root == null) return;

		Rect innerBounds = ComputeInnerBounds(root);
		Rect groupBounds = root.Bounds;

		if (innerBounds.Width <= 0 || innerBounds.Height <= 0) return;

		// 如果内部内容的左上角不在 (0,0) 附近，就需要平移
		double dx = innerBounds.X < 0 ? -innerBounds.X : 0;
		double dy = innerBounds.Y < 0 ? -innerBounds.Y : 0;

		// 也考虑内容超出 groupBounds 右/下边界的情况
		double extraRight = (innerBounds.X + innerBounds.Width) - (groupBounds.X + groupBounds.Width);
		double extraBottom = (innerBounds.Y + innerBounds.Height) - (groupBounds.Y + groupBounds.Height);
		dx -= Math.Max(0, extraRight);
		dy -= Math.Max(0, extraBottom);

		if (dx == 0 && dy == 0) return;

		// 构建一个新的 DrawingGroup，给原始 root 套 TranslateTransform
		var wrapper = new DrawingGroup();
		wrapper.Children.Add(root);
		wrapper.Transform = new TranslateTransform(dx, dy);
		img.Drawing = wrapper;
	}

	private static Rect ComputeInnerBounds(Drawing d)
	{
		if (d is GeometryDrawing gd)
		{
			var g = gd.Geometry;
			if (g == null) return Rect.Empty;
			// 跳过透明背景矩形
			if (gd.Brush is SolidColorBrush scb && scb.Color.A == 0) return Rect.Empty;
			return g.Bounds;
		}
		if (d is GlyphRunDrawing grd)
		{
			return grd.GlyphRun.ComputeAlignmentBox();
		}
		if (d is ImageDrawing id)
		{
			return id.Rect;
		}
		if (d is DrawingGroup dg)
		{
			Rect union = Rect.Empty;
			foreach (var child in dg.Children)
			{
				Rect childBounds = ComputeInnerBounds(child);
				if (childBounds != Rect.Empty)
				{
					if (union == Rect.Empty) union = childBounds;
					else union.Union(childBounds);
				}
			}
			// 应用 DrawingGroup 的 Transform
			if (dg.Transform != null && union != Rect.Empty)
			{
				union = dg.Transform.TransformBounds(union);
			}
			return union;
		}
		return Rect.Empty;
	}
}
