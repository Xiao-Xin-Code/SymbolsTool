using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SymbolsTool.Helpers;

public static class AppIcon
{
	private const float DesignSize = 512f;

	private static readonly int[] IconSizes = new int[8] { 16, 20, 24, 32, 40, 48, 64, 256 };

	private static ImageSource? _windowIcon;

	public static ImageSource WindowIcon => _windowIcon ?? (_windowIcon = CreateImageSource(256));

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint hObject);

	public static Icon CreateTrayIcon(int pixelSize = 32)
	{
		List<(int, byte[])> list = new List<(int, byte[])>();
		int[] iconSizes = IconSizes;
		foreach (int num in iconSizes)
		{
			using Bitmap bitmap = Render(num);
			using MemoryStream memoryStream = new MemoryStream();
			bitmap.Save(memoryStream, ImageFormat.Png);
			list.Add((num, memoryStream.ToArray()));
		}
		using MemoryStream memoryStream2 = new MemoryStream();
		using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream2, Encoding.UTF8, leaveOpen: true))
		{
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)1);
			binaryWriter.Write((ushort)list.Count);
			int num2 = 6 + 16 * list.Count;
			foreach (var (num3, array) in list)
			{
				binaryWriter.Write((byte)num3);
				binaryWriter.Write((byte)num3);
				binaryWriter.Write((byte)0);
				binaryWriter.Write((byte)0);
				binaryWriter.Write((ushort)1);
				binaryWriter.Write((ushort)32);
				binaryWriter.Write(array.Length);
				binaryWriter.Write(num2);
				num2 += array.Length;
			}
			foreach (var item2 in list)
			{
				byte[] item = item2.Item2;
				binaryWriter.Write(item);
			}
		}
		memoryStream2.Position = 0L;
		int num4 = Math.Max(16, pixelSize);
		return new Icon(memoryStream2, num4, num4);
	}

	private static Bitmap Render(int size)
	{
		Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		using Graphics graphics = Graphics.FromImage(bitmap);
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
		graphics.Clear(System.Drawing.Color.Transparent);
		float num = (float)size * 0.4531f;
		float num2 = (float)size / 2f;
		float num3 = (float)size / 2f;
		using GraphicsPath graphicsPath = new GraphicsPath();
		graphicsPath.AddEllipse(num2 - num, num3 - num, num * 2f, num * 2f);
		using (System.Drawing.Drawing2D.LinearGradientBrush linearGradientBrush = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(num2 - num, num3 - num), new PointF(num2 + num, num3 + num), System.Drawing.Color.FromArgb(155, 164, 251), System.Drawing.Color.FromArgb(69, 59, 200)))
		{
			linearGradientBrush.InterpolationColors = new ColorBlend
			{
				Colors = new System.Drawing.Color[3]
				{
					System.Drawing.Color.FromArgb(155, 164, 251),
					System.Drawing.Color.FromArgb(99, 102, 241),
					System.Drawing.Color.FromArgb(69, 59, 200)
				},
				Positions = new float[3] { 0f, 0.55f, 1f }
			};
			graphics.FillPath(linearGradientBrush, graphicsPath);
		}
		graphics.SetClip(graphicsPath);
		using (GraphicsPath graphicsPath2 = new GraphicsPath())
		{
			float num4 = num * 0.45f;
			float num5 = num2 - num * 0.35f;
			float num6 = num3 - num * 0.42f;
			graphicsPath2.AddEllipse(num5 - num4, num6 - num4, num4 * 2f, num4 * 2f);
			System.Drawing.Color[] array = new System.Drawing.Color[graphicsPath2.PointCount];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = System.Drawing.Color.FromArgb(0, 255, 255, 255);
			}
			using PathGradientBrush brush = new PathGradientBrush(graphicsPath2)
			{
				CenterColor = System.Drawing.Color.FromArgb(70, 255, 255, 255),
				CenterPoint = new PointF(num5, num6),
				SurroundColors = array
			};
			graphics.FillPath(brush, graphicsPath2);
		}
		graphics.ResetClip();
		graphics.ScaleTransform((float)size / 512f, (float)size / 512f);
		using (GraphicsPath graphicsPath3 = new GraphicsPath())
		{
			graphicsPath3.AddBezier(new PointF(256f, 146f), new PointF(264.4f, 218.5f), new PointF(293.5f, 247.6f), new PointF(366f, 256f));
			graphicsPath3.AddBezier(new PointF(366f, 256f), new PointF(293.5f, 264.4f), new PointF(264.4f, 293.5f), new PointF(256f, 366f));
			graphicsPath3.AddBezier(new PointF(256f, 366f), new PointF(247.6f, 293.5f), new PointF(218.5f, 264.4f), new PointF(146f, 256f));
			graphicsPath3.AddBezier(new PointF(146f, 256f), new PointF(218.5f, 247.6f), new PointF(247.6f, 218.5f), new PointF(256f, 146f));
			graphicsPath3.CloseFigure();
			using System.Drawing.Drawing2D.LinearGradientBrush brush2 = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0f, 146f), new PointF(0f, 366f), System.Drawing.Color.White, System.Drawing.Color.FromArgb(232, 233, 255));
			graphics.FillPath(brush2, graphicsPath3);
		}
		return bitmap;
	}

	private static ImageSource CreateImageSource(int size)
	{
		using Bitmap bitmap = Render(size);
		nint hbitmap = bitmap.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
		try
		{
			BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			bitmapSource.Freeze();
			return bitmapSource;
		}
		finally
		{
			DeleteObject(hbitmap);
		}
	}
}
