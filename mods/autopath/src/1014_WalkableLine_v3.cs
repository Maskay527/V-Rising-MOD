using System;
using System.Numerics;

namespace VRisingAutoPath;

internal static class WalkableLine
{
    // Static map cells are 0.5 world units wide. Sample more densely, including the character footprint.
    internal static bool CanTraverse(Vector2 from, Vector2 to, float radius, Func<Vector2,bool> isWalkable)
    {
        float length = Vector2.Distance(from,to);
        if (!float.IsFinite(length) || !float.IsFinite(radius) || length > 64 || radius < 0) return false;
        int steps = Math.Max(1,(int)Math.Ceiling(length/0.2f));
        for(int i=0;i<=steps;i++)
        {
            var p = Vector2.Lerp(from,to,(float)i/steps);
            if (!isWalkable(p)) return false;
            for(int j=0;j<8;j++)
            {
                float angle=j*MathF.PI/4;
                if (!isWalkable(p+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*radius)) return false;
            }
        }
        return true;
    }
}


