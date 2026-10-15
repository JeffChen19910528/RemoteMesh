namespace RemoteDesktop.UI.Services;

/// <summary>Maps a point on the (letterboxed) picture to the 0–32767 coordinates the host expects.</summary>
public static class CoordinateMapper
{
    public const int Max = 32767;

    /// <summary>
    /// The picture is drawn with <c>Stretch=Uniform</c>, so there can be bars around it. A point on a bar is not
    /// on the remote screen: returns null instead of a coordinate pinned to the edge.
    /// </summary>
    public static (int X, int Y)? Map(double x, double y, double viewWidth, double viewHeight, int frameWidth, int frameHeight)
    {
        if (viewWidth <= 0 || viewHeight <= 0 || frameWidth <= 0 || frameHeight <= 0) return null;

        var scale = Math.Min(viewWidth / frameWidth, viewHeight / frameHeight);
        var drawnW = frameWidth * scale;
        var drawnH = frameHeight * scale;
        var left = (viewWidth - drawnW) / 2;
        var top = (viewHeight - drawnH) / 2;

        var px = x - left;
        var py = y - top;
        if (px < 0 || py < 0 || px > drawnW || py > drawnH) return null;

        return ((int)Math.Round(px / drawnW * Max), (int)Math.Round(py / drawnH * Max));
    }
}
