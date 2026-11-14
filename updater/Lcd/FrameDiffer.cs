using System.Drawing;

namespace ScreenUpdater.Lcd;

/// <summary>Finds the rectangles that differ between two RGB565 frames.</summary>
public static class FrameDiffer
{
    const int Tile = 16;

    public static List<Rectangle> Diff(ushort[]? previous, ushort[] current, int width, int height)
    {
        if (previous is null)
            return [new Rectangle(0, 0, width, height)];

        int cols = (width + Tile - 1) / Tile;
        int rows = (height + Tile - 1) / Tile;
        var dirty = new bool[rows, cols];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            int ty = y / Tile;
            for (int tx = 0; tx < cols; tx++)
            {
                if (dirty[ty, tx]) continue;
                int start = rowStart + tx * Tile;
                int len = Math.Min(Tile, width - tx * Tile);
                if (!previous.AsSpan(start, len).SequenceEqual(current.AsSpan(start, len)))
                    dirty[ty, tx] = true;
            }
        }

        // Horizontal runs per tile row, extended downward while the next row has the same run.
        var rects = new List<Rectangle>();
        var open = new Dictionary<(int From, int To), int>();
        for (int ty = 0; ty <= rows; ty++)
        {
            var runs = new HashSet<(int From, int To)>();
            if (ty < rows)
            {
                int tx = 0;
                while (tx < cols)
                {
                    if (!dirty[ty, tx]) { tx++; continue; }
                    int from = tx;
                    while (tx < cols && dirty[ty, tx]) tx++;
                    runs.Add((from, tx));
                }
            }

            foreach (var (run, startRow) in open.ToArray())
            {
                if (runs.Contains(run)) continue;
                open.Remove(run);
                int x = run.From * Tile;
                int y = startRow * Tile;
                rects.Add(new Rectangle(
                    x, y,
                    Math.Min(run.To * Tile, width) - x,
                    Math.Min(ty * Tile, height) - y));
            }

            foreach (var run in runs)
                open.TryAdd(run, ty);
        }

        return rects;
    }
}
