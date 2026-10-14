using System;
using System.Collections.Generic;
using System.Numerics;

namespace VRisingAutoPath;

// Independent of Unity so arrival, corners, interruption and watchdogs can be exercised deterministically.
internal sealed class PathFollower
{
    private readonly List<Vector2> _points = new();
    private Vector2 _previous;
    private float _bestDistance;
    private float _withoutProgress;
    private float _elapsed;
    internal bool Active { get; private set; }
    internal string Reason { get; private set; } = "idle";
    internal int Index { get; private set; }
    internal int Count => _points.Count;
    internal Vector2 Goal { get; private set; }
    internal Vector2 Next => Active ? _points[Index] : Goal;

    internal bool Start(IEnumerable<Vector2> points, Vector2 position, Vector2 goal)
    {
        Stop("invalid-path");
        if (!Finite(position) || !Finite(goal)) return false;
        var candidate = new List<Vector2>();
        foreach (var point in points)
        {
            if (!Finite(point) || candidate.Count >= 16384) return false;
            if (candidate.Count == 0 || Vector2.DistanceSquared(candidate[^1], point) > 0.0025f)
                candidate.Add(point);
        }
        if (candidate.Count == 0) return false;
        // Native guidance may store a reverse path. Orient using both ends, never choose a shortcut in the middle.
        float forward = Vector2.Distance(candidate[0], position) + Vector2.Distance(candidate[^1], goal);
        float reverse = Vector2.Distance(candidate[^1], position) + Vector2.Distance(candidate[0], goal);
        if (reverse < forward) candidate.Reverse();
        // Do not append an unverified straight segment to an incomplete native path.
        if (Vector2.Distance(candidate[^1], goal) > 2f) return false;
        if (Vector2.Distance(candidate[0], position) > 8f) return false;
        _points.Clear();
        _points.AddRange(candidate);
        Goal = goal;
        _previous = position;
        Index = 0;
        _bestDistance = float.PositiveInfinity;
        _withoutProgress = _elapsed = 0;
        Reason = "running";
        Active = true;
        return true;
    }

    internal Vector2 Tick(Vector2 position, float dt, float speed)
    {
        if (!Active) return Vector2.Zero;
        if (!Finite(position) || !float.IsFinite(dt) || !float.IsFinite(speed) || dt <= 0 || speed <= 0)
        { Stop("invalid-state"); return Vector2.Zero; }
        if (dt > 1f) { Stop("frame-gap"); return Vector2.Zero; }
        float displacement = Vector2.Distance(position, _previous);
        _previous = position;
        if (displacement > Math.Max(6f, speed * dt * 4f))
        { Stop("teleport"); return Vector2.Zero; }
        _elapsed += dt;
        if (_elapsed > 1800f) { Stop("time-limit"); return Vector2.Zero; }
        float reach = Math.Clamp(speed * dt + 0.12f, 0.35f, 0.7f);
        while (Index < _points.Count - 1)
        {
            // Dense local-grid points need a smaller tolerance so turns do not cut across obstacles.
            bool dense = Vector2.Distance(_points[Index],_points[Index+1]) < 1.1f ||
                (Index>0 && Vector2.Distance(_points[Index-1],_points[Index]) < 1.1f);
            if(Vector2.Distance(position,_points[Index]) > (dense ? Math.Min(reach,0.15f) : reach)) break;
            Index++;
            _bestDistance = float.PositiveInfinity;
            _withoutProgress = 0;
        }
        if (Index == _points.Count-1 && Vector2.Distance(position, Goal) <= 0.8f)
        { Stop("arrived"); return Vector2.Zero; }
        var offset = _points[Index] - position;
        float distance = offset.Length();
        if (distance <= 0.05f)
        { Stop("path-ended-short"); return Vector2.Zero; }
        if (distance < _bestDistance - 0.1f)
        { _bestDistance = distance; _withoutProgress = 0; }
        else _withoutProgress += dt;
        if (_withoutProgress >= 4f)
        { Stop("stuck"); return Vector2.Zero; }
        // A fractional input near a waypoint prevents fast characters from oscillating across it.
        return offset / distance * Math.Min(1f, distance / (speed * dt));
    }

    internal void Stop(string reason)
    {
        Active = false;
        Reason = reason;
    }

    private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
}


