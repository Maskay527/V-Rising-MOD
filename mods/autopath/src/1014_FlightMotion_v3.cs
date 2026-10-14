using System;
using System.Numerics;

namespace VRisingAutoPath;

// Client Shapeshift.Speed can be zero even while native flight is active.
// Estimate horizontal speed from actual motion under full input, not by changing any game stats.
internal sealed class FlightMotion
{
    private Vector2 _previous;
    private double _lastTime;
    private float _distance, _duration;
    internal float Speed { get; private set; }
    internal void Reset(Vector2 position,double time,float fallback)
    {
        _previous=position; _lastTime=time; _distance=_duration=0;
        Speed=float.IsFinite(fallback) && fallback>0 ? fallback : 1;
    }
    internal void Sample(Vector2 position,double time,float previousInput)
    {
        float dt=(float)(time-_lastTime), distance=Vector2.Distance(position,_previous);
        _previous=position; _lastTime=time;
        if(dt<=0 || dt>1 || !float.IsFinite(distance) || previousInput<0.9f)
        { _distance=_duration=0;return; }
        _distance+=distance; _duration+=dt;
        if(_duration<0.15f) return;
        if(_distance>0.05f)
        {
            float measured=_distance/_duration;
            if(float.IsFinite(measured) && measured>0) Speed=(Speed+measured)*0.5f;
        }
        _distance=_duration=0;
    }
    internal Vector2 LimitNearGoal(Vector2 input,float distance,float dt)
    {
        float magnitude=input.Length();
        if(magnitude<=0) return Vector2.Zero;
        // Allow for discrete server movement ticks rather than using only the faster render interval.
        float limit=Math.Min(magnitude,distance/(Speed*Math.Max(dt,0.25f)));
        return input/magnitude*limit;
    }
}
