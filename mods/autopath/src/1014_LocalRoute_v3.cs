using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace VRisingAutoPath;

// Connect only the two ends of the game's route. Its road waypoints remain unchanged.
internal sealed class LocalRoute
{
    private readonly List<Vector2> _native;
    private readonly Vector2 _start, _goal;
    private readonly float _radius;
    private readonly Func<Vector2,bool> _walkable;
    private readonly List<Vector2> _prefix = new();
    private LocalSearch _search;
    private int _stage;
    internal List<Vector2> Points { get; private set; }
    internal string Reason { get; private set; } = "planning";
    internal bool Done => Reason != "planning";
    internal string Stage => _stage == 0 ? "start" : "end";
    internal int StartConnectorPoints { get; private set; }
    internal int EndConnectorPoints { get; private set; }
    internal int TrimmedStartPoints { get; private set; }
    internal int TrimmedEndPoints { get; private set; }

    internal LocalRoute(IReadOnlyList<Vector2> native, Vector2 start, Vector2 goal, float radius, Func<Vector2,bool> walkable)
    {
        _native = new List<Vector2>(native); _start = start; _goal = goal; _radius = radius; _walkable = walkable;
        if (_native.Count == 0 || _native.Count > 16384) { Reason = "invalid-native-path"; return; }
        foreach(var p in _native) if(!float.IsFinite(p.X) || !float.IsFinite(p.Y)) { Reason = "invalid-native-path"; return; }
        float forward = Vector2.Distance(_native[0],start)+Vector2.Distance(_native[^1],goal);
        float reverse = Vector2.Distance(_native[^1],start)+Vector2.Distance(_native[0],goal);
        if(reverse < forward) _native.Reverse();
        // Native flood-fill endpoints use tile centres; their footprint can overlap an edge.
        // Join a nearby valid road point rather than reject the whole road route.
        var first=_native[0]; var last=_native[^1];
        while(_native.Count>1 && TrimmedStartPoints<32 && !WalkableLine.CanTraverse(_native[0],_native[0],radius,walkable))
        {
            if(Vector2.Distance(first,_native[1])>8) break;
            _native.RemoveAt(0); TrimmedStartPoints++;
        }
        while(_native.Count>1 && TrimmedEndPoints<32 && !WalkableLine.CanTraverse(_native[^1],_native[^1],radius,walkable))
        {
            if(Vector2.Distance(last,_native[^2])>8) break;
            _native.RemoveAt(_native.Count-1); TrimmedEndPoints++;
        }
    }

    internal void Advance(int maxNodes = 64, double milliseconds = 2)
    {
        if(Done) return;
        _search ??= new LocalSearch(_stage == 0 ? _start : _native[^1], _stage == 0 ? _native[0] : _goal, _radius, _walkable);
        _search.Advance(maxNodes,milliseconds);
        if(!_search.Done) return;
        if(_search.Reason != "found") { Reason = Stage+"-"+_search.Reason; return; }
        if(_stage == 0)
        {
            _prefix.AddRange(_search.Points);
            StartConnectorPoints = _search.Points.Count;
            _stage = 1; _search = null;
            return;
        }
        Points = new List<Vector2>(_prefix);
        EndConnectorPoints = _search.Points.Count;
        for(int i=1;i<_native.Count;i++) Points.Add(_native[i]);
        for(int i=1;i<_search.Points.Count;i++) Points.Add(_search.Points[i]);
        Reason = "found";
    }
}

// Incremental A* on the map's static walkable mask. All edges check the character footprint.
internal sealed class LocalSearch
{
    private const float Cell = 0.5f;
    private readonly Vector2 _from, _to;
    private readonly float _radius;
    private readonly Func<Vector2,bool> _walkable;
    private readonly PriorityQueue<(int x,int y,float cost),float> _open = new();
    private readonly Dictionary<(int x,int y),float> _cost = new();
    private readonly Dictionary<(int x,int y),(int x,int y)> _parent = new();
    private readonly Dictionary<(int x,int y),bool> _clear = new();
    private readonly int _minX,_maxX,_minY,_maxY;
    private bool _initialized;
    private int _expanded;
    internal string Reason { get; private set; } = "planning";
    internal bool Done => Reason != "planning";
    internal List<Vector2> Points { get; private set; }

    internal LocalSearch(Vector2 from, Vector2 to, float radius, Func<Vector2,bool> walkable)
    {
        _from=from; _to=to; _radius=radius; _walkable=walkable;
        float length=Vector2.Distance(from,to);
        if(!float.IsFinite(length) || !float.IsFinite(radius) || radius<0) { Reason="invalid"; return; }
        if(length>128) { Reason="too-far"; return; }
        var delta=(to-from)/Cell;
        _minX=(int)MathF.Floor(Math.Min(0,delta.X))-32; _maxX=(int)MathF.Ceiling(Math.Max(0,delta.X))+32;
        _minY=(int)MathF.Floor(Math.Min(0,delta.Y))-32; _maxY=(int)MathF.Ceiling(Math.Max(0,delta.Y))+32;
    }
    private Vector2 Position((int x,int y) n) => _from+new Vector2(n.x*Cell,n.y*Cell);
    private bool Clear((int x,int y) n)
    {
        if(_clear.TryGetValue(n,out bool value)) return value;
        value=WalkableLine.CanTraverse(Position(n),Position(n),_radius,_walkable);
        _clear[n]=value; return value;
    }
    private int BlockedFootprint(Vector2 p)
    {
        if(!_walkable(p)) return 9;
        int blocked=0;
        for(int i=0;i<8;i++)
        {
            float angle=i*MathF.PI/4;
            if(!_walkable(p+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*_radius)) blocked++;
        }
        return blocked;
    }
    private bool CanLeaveExistingOverlap(Vector2 to)
    {
        int previous=BlockedFootprint(_from);
        if(previous==9) return false;
        int steps=Math.Max(1,(int)MathF.Ceiling(Vector2.Distance(_from,to)/0.1f));
        for(int i=1;i<=steps;i++)
        {
            int blocked=BlockedFootprint(Vector2.Lerp(_from,to,(float)i/steps));
            if(blocked>previous) return false;
            previous=blocked;
        }
        return previous==0;
    }
    internal void Advance(int maxNodes=64,double milliseconds=2)
    {
        if(Done) return;
        var timer=Stopwatch.StartNew();
        if(!_initialized)
        {
            _initialized=true;
            if(!WalkableLine.CanTraverse(_to,_to,_radius,_walkable)) { Reason="target-blocked"; return; }
            bool sourceClear=Clear((0,0));
            if(sourceClear && WalkableLine.CanTraverse(_from,_to,_radius,_walkable))
            { Points=new List<Vector2>{_from,_to}; Reason="found"; return; }
            _cost[(0,0)]=0;
            if(sourceClear) _open.Enqueue((0,0,0),Vector2.Distance(_from,_to));
            else
            {
                // The character is already here. Permit only a short escape that reduces existing
                // footprint overlap, never a blocked centre or increasing overlap into a wall.
                for(int x=-3;x<=3;x++) for(int y=-3;y<=3;y++)
                {
                    var seed=(x,y);
                    var point=Position(seed); float distance=Vector2.Distance(_from,point);
                    if(distance==0 || distance>1.5f || !Clear(seed) || !CanLeaveExistingOverlap(point)) continue;
                    _cost[seed]=distance; _parent[seed]=(0,0);
                    _open.Enqueue((x,y,distance),distance+Vector2.Distance(point,_to));
                }
                if(_open.Count==0) { Reason="source-blocked"; return; }
            }
        }
        for(int count=0;count<maxNodes && timer.Elapsed.TotalMilliseconds<milliseconds;count++)
        {
            if(!_open.TryDequeue(out var current,out _)) { Reason="unreachable"; return; }
            var node=(current.x,current.y);
            if(current.cost > _cost[node]+0.0001f) continue;
            if(++_expanded>20000) { Reason="search-limit"; return; }
            var position=Position(node);
            if(Vector2.Distance(position,_to)<=0.9f && WalkableLine.CanTraverse(position,_to,_radius,_walkable))
            {
                Points=new List<Vector2>{position};
                var back=node;
                while(_parent.TryGetValue(back,out var previous)) { back=previous; Points.Add(Position(back)); }
                Points.Reverse();
                if(Vector2.DistanceSquared(Points[^1],_to)>0.000001f) Points.Add(_to);
                Reason="found"; return;
            }
            for(int dx=-1;dx<=1;dx++) for(int dy=-1;dy<=1;dy++)
            {
                if(dx==0 && dy==0) continue;
                var next=(x:node.x+dx,y:node.y+dy);
                if(next.x<_minX || next.x>_maxX || next.y<_minY || next.y>_maxY) continue;
                float cost=current.cost+Cell*(dx==0 || dy==0 ? 1 : MathF.Sqrt(2));
                if(_cost.TryGetValue(next,out var existing) && cost>=existing-0.0001f) continue;
                if(!Clear(next) || !WalkableLine.CanTraverse(position,Position(next),_radius,_walkable)) continue;
                _cost[next]=cost; _parent[next]=node;
                _open.Enqueue((next.x,next.y,cost),cost+Vector2.Distance(Position(next),_to));
            }
        }
    }
}

