using System;

namespace VRisingMoveSpeed;

internal static class SpeedMath
{
    internal static bool Near(float left, float right) =>
        MathF.Abs(left - right) <= MathF.Max(0.0001f, MathF.Max(MathF.Abs(left), MathF.Abs(right)) * 0.00002f);

    internal static float Target(float baseline, float multiplier)
    {
        if (!float.IsFinite(baseline) || !float.IsFinite(multiplier) || multiplier < 0.1f || multiplier > 10f)
            throw new ArgumentOutOfRangeException(nameof(multiplier), "速度和倍率必须为有效有限数值，倍率范围 0.1～10。");
        float target = baseline * multiplier;
        if (!float.IsFinite(target)) throw new OverflowException("目标速度超出 float 范围。");
        return target;
    }

}

// Only the runtime Mounter's plain float fields use this; permanent horse stats are untouched.
internal sealed class RawScaleState
{
    internal float Baseline;
    internal float LastWritten;
    internal bool HasWrite;

    internal float Apply(float current, float multiplier)
    {
        if (!HasWrite || !SpeedMath.Near(current, LastWritten)) Baseline = current;
        float result = SpeedMath.Target(Baseline, multiplier);
        LastWritten = result;
        HasWrite = true;
        return result;
    }

    internal float Restore(float current)
    {
        float result = HasWrite && SpeedMath.Near(current, LastWritten) ? Baseline : current;
        HasWrite = false;
        return result;
    }
}



