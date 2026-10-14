using System;

namespace VRisingMoveSpeed;

internal static class SpeedMathTests
{
    private static int _assertions;
    private static void Equal(float actual, float expected, string name)
    {
        _assertions++;
        if (!SpeedMath.Near(actual, expected)) throw new Exception(name + ": " + actual + " != " + expected);
    }
    private static void Reject(Action action, string name)
    {
        _assertions++;
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception(name + " did not reject input");
    }
    public static int Main()
    {
        foreach (float multiplier in new[] { 0.1f, 0.5f, 1f, 1.5f, 2f, 3f, 10f })
        {
            foreach (float speed in new[] { 0f, 4f, 5.6f, 10f, 11.2f, 16.8f })
                Equal(SpeedMath.Target(speed, multiplier), speed * multiplier, "ratio");
            var walk = new RawScaleState();
            var horse = new RawScaleState();
            float walking = walk.Apply(4, multiplier), mounted = horse.Apply(11.2f, multiplier);
            for (int repeat = 0; repeat < 1000; repeat++)
            {
                walking = walk.Apply(walking, multiplier);
                mounted = horse.Apply(mounted, multiplier);
            }
            Equal(walking, 4 * multiplier, "walking no accumulation");
            Equal(mounted, 11.2f * multiplier, "mounted no accumulation");
            Equal(walk.Restore(walking), 4, "walking restore");
            Equal(horse.Restore(mounted), 11.2f, "mount restore");
        }
        var change = new RawScaleState();
        Equal(change.Apply(10, 2), 20, "initial mount");
        Equal(change.Apply(20, 3), 30, "multiplier change without multiplying modified value");
        Equal(change.Apply(12, 3), 36, "game replaced baseline");
        Equal(change.Restore(13), 13, "preserve external write on restore");
        var playerA = new RawScaleState();
        var playerB = new RawScaleState();
        Equal(playerA.Apply(10, 2), 20, "player A");
        Equal(playerB.Apply(12, 2), 24, "player B");
        Equal(playerA.Restore(20), 10, "player A independent");
        Equal(playerB.Restore(24), 12, "player B independent");
        Reject(() => SpeedMath.Target(4, float.NaN), "NaN");
        Reject(() => SpeedMath.Target(4, float.PositiveInfinity), "infinity");
        Reject(() => SpeedMath.Target(4, 0), "zero multiplier");
        Reject(() => SpeedMath.Target(4, 11), "out of range");
        Console.WriteLine("PASS " + _assertions + " assertions; includes 14000 repeated updates.");
        return 0;
    }
}



