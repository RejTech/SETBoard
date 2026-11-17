using Avalonia;

namespace SETBoard.Board;

/// <summary>
/// 轻量笔尖预测器：基于最近 N 个采样点的速度与加速度做线性外推，
/// 在真实笔尖前方预绘一段轨迹，降低感知延迟。
/// </summary>
internal sealed class InkPredictor
{
    /// <summary>默认预测步长（屏幕像素）。</summary>
    private const double DefaultPredictionStep = 12.0;

    /// <summary>预测点历史长度，用于计算速度与加速度。</summary>
    private static readonly int HistorySize = 5;

    private readonly Point[] _history = new Point[HistorySize];
    private int _head;
    private int _count;
    private double _predictionStep;

    /// <summary>笔尖宽度（屏幕像素），用于自适应预测步长。</summary>
    public void SetInkWidth(double screenPixelWidth)
    {
        // 粗笔感知延迟更高，适当增加预测距离
        _predictionStep = Math.Clamp(screenPixelWidth * 0.3, 8.0, 18.0);
    }

    private double PredictionStep => (_predictionStep > 0) ? _predictionStep : DefaultPredictionStep;

    /// <summary>记录一个真实采样点。</summary>
    public void Record(Point point)
    {
        _history[_head] = point;
        _head = (_head + 1) % HistorySize;
        if (_count < HistorySize) _count++;
    }

    /// <summary>预测下一个笔尖位置（世界坐标）。返回 null 表示数据不足。</summary>
    public Point? Predict(double zoom)
    {
        if (_count < 2) return null;

        // 取最近的点（逆序）
        int idx = (_head - 1 + HistorySize) % HistorySize;
        var last = _history[idx];

        // 至少需要两个点来计算速度
        if (_count < 2) return null;
        var prev = _history[(idx - 1 + HistorySize) % HistorySize];

        Vector velocity = last - prev;

        // 如果两点距离极小，说明用户停笔了，不预测
        if (velocity.Length < 0.5) return null;

        // 如果有第三个点，用加速度修正
        if (_count >= 3)
        {
            var older = _history[(idx - 2 + HistorySize) % HistorySize];
            Vector prevVelocity = last - older;
            Vector acceleration = velocity - prevVelocity;

            // 预测：v_next = v_current + a * dt（dt=1 帧）
            velocity = velocity + acceleration * 0.5;

            // 限制最大加速度，避免急转弯时预测过冲
            if (velocity.Length > 80)
            {
                velocity = velocity.Normalize() * 80;
            }
        }

        // 转换到世界坐标并外推
        Vector worldVelocity = velocity / zoom;
        Point predicted = last + worldVelocity * PredictionStep;

        return predicted;
    }

    /// <summary>抬笔/停笔后重置预测器。</summary>
    public void Reset()
    {
        _head = 0;
        _count = 0;
    }
}
