using Godot;

internal sealed partial class FrameTimeGraph : Control
{
    private const int SampleCount = 120;
    private const float TargetFrameMs = 16.67f;
    private const float HitchFrameMs = 33.3f;
    private const float MaxGraphMs = 66.7f;

    private readonly float[] _samples = new float[SampleCount];
    private int _nextSample;
    private int _sampleCount;

    public void AddSample(double frameMs)
    {
        _samples[_nextSample] = (float)Math.Clamp(frameMs, 0d, MaxGraphMs);
        _nextSample = (_nextSample + 1) % _samples.Length;
        _sampleCount = Math.Min(_sampleCount + 1, _samples.Length);

        if (Visible)
        {
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X <= 1 || size.Y <= 1)
        {
            return;
        }

        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.03f, 0.05f, 0.06f, 0.78f), filled: true);
        DrawFrameLine(TargetFrameMs, new Color(0.30f, 0.68f, 0.78f, 0.55f));
        DrawFrameLine(HitchFrameMs, new Color(1.0f, 0.70f, 0.18f, 0.68f));

        if (_sampleCount == 0)
        {
            return;
        }

        var barWidth = Math.Max(1f, size.X / SampleCount);
        for (var i = 0; i < _sampleCount; i++)
        {
            var sampleIndex = (_nextSample - _sampleCount + i + _samples.Length) % _samples.Length;
            var ms = _samples[sampleIndex];
            var normalized = Math.Clamp(ms / MaxGraphMs, 0f, 1f);
            var x = i * barWidth;
            var y = size.Y - (normalized * size.Y);
            var color = ms >= HitchFrameMs
                ? new Color(1.0f, 0.46f, 0.24f, 0.95f)
                : new Color(0.53f, 0.82f, 0.94f, 0.92f);
            DrawLine(new Vector2(x, size.Y), new Vector2(x, y), color, Math.Max(1f, barWidth - 1f));
        }
    }

    private void DrawFrameLine(float frameMs, Color color)
    {
        var y = Size.Y - (Math.Clamp(frameMs / MaxGraphMs, 0f, 1f) * Size.Y);
        DrawLine(new Vector2(0, y), new Vector2(Size.X, y), color, 1f);
    }
}
