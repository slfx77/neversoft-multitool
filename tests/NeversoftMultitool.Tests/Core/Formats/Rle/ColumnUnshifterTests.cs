using NeversoftMultitool.Core.Formats.Rle;
using System.Reflection;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public class ColumnUnshifterTests
{
    // Access internal types via reflection since ColumnUnshifter and RgbColor are internal
    private static readonly Type RgbColorType = typeof(RleImage).Assembly.GetType("NeversoftMultitool.Core.Formats.Rle.RgbColor")!;
    private static readonly Type UnshifterType = typeof(RleImage).Assembly.GetType("NeversoftMultitool.Core.Formats.Rle.ColumnUnshifter")!;

    private static object MakeColor(byte r, byte g, byte b)
    {
        return Activator.CreateInstance(RgbColorType, r, g, b)!;
    }

    [Fact]
    public void Unshift_NoBlueMarker_ReturnsUnchanged()
    {
        // Canvas without blue markers at end of first row should pass through unchanged
        var canvas = MakeCanvas(4, 3, (0, 0, 0));
        var result = CallUnshift(canvas);
        Assert.Equal(3, GetCanvasHeight(result));
    }

    [Fact]
    public void Unshift_EmptyCanvas_ReturnsEmpty()
    {
        var canvas = MakeEmptyCanvas();
        var result = CallUnshift(canvas);
        Assert.Equal(0, GetCanvasHeight(result));
    }

    [Fact]
    public void Unshift_WithBlueMarker1_PerformsUnshift()
    {
        // Create a 4-pixel wide, 3-row canvas with blue marker (0,0,144) at end of first row
        var canvas = MakeCanvasWithMarker(4, 3, 0, 0, 144);

        var result = CallUnshift(canvas);

        // Should have same dimensions
        Assert.Equal(3, GetCanvasHeight(result));
        Assert.Equal(4, GetRowWidth(result, 0));
    }

    [Fact]
    public void Unshift_WithBlueMarker2_PerformsUnshift()
    {
        // Blue marker (0,0,208) should also trigger unshifting
        var canvas = MakeCanvasWithMarker(4, 3, 0, 0, 208);

        var result = CallUnshift(canvas);
        Assert.Equal(3, GetCanvasHeight(result));
    }

    // Helper: Create empty canvas (List<List<RgbColor>>)
    private static object MakeEmptyCanvas()
    {
        var listType = typeof(List<>).MakeGenericType(typeof(List<>).MakeGenericType(RgbColorType));
        return Activator.CreateInstance(listType)!;
    }

    // Helper: Create canvas filled with a single color
    private static object MakeCanvas(int width, int height, (byte r, byte g, byte b) fill)
    {
        var rowListType = typeof(List<>).MakeGenericType(RgbColorType);
        var canvasType = typeof(List<>).MakeGenericType(rowListType);
        var canvas = Activator.CreateInstance(canvasType)!;
        var addRow = canvasType.GetMethod("Add")!;

        for (var y = 0; y < height; y++)
        {
            var row = Activator.CreateInstance(rowListType)!;
            var addPixel = rowListType.GetMethod("Add")!;
            for (var x = 0; x < width; x++)
            {
                addPixel.Invoke(row, [MakeColor(fill.r, fill.g, fill.b)]);
            }
            addRow.Invoke(canvas, [row]);
        }

        return canvas;
    }

    // Helper: Create canvas with blue marker at end of first row
    private static object MakeCanvasWithMarker(int width, int height, byte mr, byte mg, byte mb)
    {
        var rowListType = typeof(List<>).MakeGenericType(RgbColorType);
        var canvasType = typeof(List<>).MakeGenericType(rowListType);
        var canvas = Activator.CreateInstance(canvasType)!;
        var addRow = canvasType.GetMethod("Add")!;

        for (var y = 0; y < height; y++)
        {
            var row = Activator.CreateInstance(rowListType)!;
            var addPixel = rowListType.GetMethod("Add")!;
            for (var x = 0; x < width; x++)
            {
                if (y == 0 && x == width - 1)
                    addPixel.Invoke(row, [MakeColor(mr, mg, mb)]);
                else
                    addPixel.Invoke(row, [MakeColor(128, 128, 128)]);
            }
            addRow.Invoke(canvas, [row]);
        }

        return canvas;
    }

    private static object CallUnshift(object canvas)
    {
        var method = UnshifterType.GetMethod("Unshift", BindingFlags.Public | BindingFlags.Static)!;
        return method.Invoke(null, [canvas])!;
    }

    private static int GetCanvasHeight(object canvas)
    {
        return (int)canvas.GetType().GetProperty("Count")!.GetValue(canvas)!;
    }

    private static int GetRowWidth(object canvas, int rowIndex)
    {
        var indexer = canvas.GetType().GetProperty("Item")!;
        var row = indexer.GetValue(canvas, [rowIndex])!;
        return (int)row.GetType().GetProperty("Count")!.GetValue(row)!;
    }
}
