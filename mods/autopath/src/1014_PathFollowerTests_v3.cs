using System;
using System.Numerics;
using VRisingAutoPath;

internal static class PathFollowerTests
{
    private static int _assertions;
    private static void Check(bool condition, string name)
    {
        _assertions++;
        if (!condition) throw new Exception(name);
    }
    public static void Main()
    {
        foreach(float speed in new[]{4f,12f})
        foreach(float dt in new[]{0.016f,0.05f,0.25f})
        {
            var f = new PathFollower();
            var position = Vector2.Zero;
            var goal = new Vector2(8,8);
            Check(f.Start(new[]{position,new Vector2(0,8),goal},position,goal), "start-corner");
            for(int i=0;f.Active && i<10000;i++)
            {
                var direction = f.Tick(position,dt,speed);
                Check(direction.Length()<=1.0001f,"normal-input-magnitude");
                position += direction*speed*dt;
                Check(position.Y>=7.25f || Math.Abs(position.X)<0.001f,"no-diagonal-shortcut");
            }
            Check(f.Reason=="arrived" && Vector2.Distance(position,goal)<=0.8f,"arrives-at-corner-route");
            Check(f.Tick(position,dt,speed)==Vector2.Zero,"no-input-after-arrival");
        }
        var reverse = new PathFollower();
        Check(reverse.Start(new[]{new Vector2(3,0),Vector2.Zero},Vector2.Zero,new Vector2(3,0)),"reverse-native-order");
        Check(reverse.Tick(Vector2.Zero,0.05f,4).X>0,"reverse-direction");
        reverse.Stop("manual-movement");
        Check(reverse.Tick(Vector2.Zero,0.05f,4)==Vector2.Zero,"manual-cancel-releases-input");
        var invalid = new PathFollower();
        Check(!invalid.Start(new[]{Vector2.Zero,new Vector2(float.NaN,0)},Vector2.Zero,new Vector2(5,0)),"invalid-native-path");
        Check(!invalid.Start(new[]{Vector2.Zero,new Vector2(1,0)},Vector2.Zero,new Vector2(10,0)),"incomplete-route-rejected");
        Check(!invalid.Start(new[]{new Vector2(30,0),new Vector2(50,0)},Vector2.Zero,new Vector2(50,0)),"distant-path-start-rejected");
        var blocked = new PathFollower();
        blocked.Start(new[]{Vector2.Zero,new Vector2(5,0)},Vector2.Zero,new Vector2(5,0));
        for(int i=0;i<100;i++)blocked.Tick(Vector2.Zero,0.05f,4);
        Check(blocked.Reason=="stuck" && !blocked.Active,"blocked-wall-stops");
        var teleported = new PathFollower();
        teleported.Start(new[]{Vector2.Zero,new Vector2(50,0)},Vector2.Zero,new Vector2(50,0));
        Check(teleported.Tick(new Vector2(20,0),0.05f,4)==Vector2.Zero && teleported.Reason=="teleport","teleport-stops");
        var paused = new PathFollower();
        paused.Start(new[]{Vector2.Zero,new Vector2(5,0)},Vector2.Zero,new Vector2(5,0));
        Check(paused.Tick(Vector2.Zero,1.1f,4)==Vector2.Zero && paused.Reason=="frame-gap","large-frame-gap-stops");
        Check(WalkableLine.CanTraverse(Vector2.Zero,new Vector2(8,0),0.5f,p=>true),"free-endpoint-connector");
        Check(!WalkableLine.CanTraverse(Vector2.Zero,new Vector2(8,0),0.5f,p=>p.X<3 || p.X>=3.5f),"thin-wall-blocks-connector");
        Check(!WalkableLine.CanTraverse(Vector2.Zero,new Vector2(8,0),0.5f,p=>Math.Abs(p.Y)<0.25f),"narrow-corridor-rejects-footprint");
        Check(!WalkableLine.CanTraverse(Vector2.Zero,new Vector2(80,0),0.5f,p=>true),"connector-distance-limit");
        Check(!WalkableLine.CanTraverse(new Vector2(float.NaN,0),Vector2.Zero,0.5f,p=>true),"invalid-connector");
        TestLocalRoutes();
        TestStraightFlight();
        TestObservedFlight();
        Console.WriteLine("PASS AutoPath v3: "+_assertions+" assertions; walking detours and straight flight at 11/33 speed across multiple frame intervals.");
    }

    private static void TestLocalRoutes()
    {
        Func<Vector2,bool> terrain = p => !((p.X>=4 && p.X<=4.5f || p.X>=22 && p.X<=22.5f) && Math.Abs(p.Y)<2);
        var start=Vector2.Zero; var goal=new Vector2(26,0);
        var route=new LocalRoute(new[]{new Vector2(20,0),new Vector2(10,0)},start,goal,0.2f,terrain);
        route.Advance(1,100);
        Check(!route.Done,"incremental-search-yields");
        for(int i=0;i<2000 && !route.Done;i++) route.Advance(64,100);
        Check(route.Reason=="found","both-connectors-detour");
        Check(route.Points[0]==start && route.Points[^1]==goal,"exact-connector-endpoints");
        Check(route.Points.IndexOf(new Vector2(10,0))>=0 && route.Points.IndexOf(new Vector2(20,0))>route.Points.IndexOf(new Vector2(10,0)),"native-road-order-preserved");
        bool startDetour=false,endDetour=false;
        for(int i=0;i<route.Points.Count;i++)
        {
            var p=route.Points[i];
            if(p.X<10 && Math.Abs(p.Y)>=2.2f) startDetour=true;
            if(p.X>20 && Math.Abs(p.Y)>=2.2f) endDetour=true;
            if(i>0) Check(WalkableLine.CanTraverse(route.Points[i-1],p,0.2f,terrain),"every-connector-edge-clear");
        }
        Check(startDetour && endDetour,"detours-around-both-walls");
        foreach(float speed in new[]{4f,12f})
        foreach(float dt in new[]{0.016f,0.05f,0.25f})
        {
            var follower=new PathFollower();
            Check(follower.Start(route.Points,start,goal),"connected-route-accepted");
            var position=start;
            for(int i=0;i<20000 && follower.Active;i++)
            {
                var input=follower.Tick(position,dt,speed);
                var next=position+input*speed*dt;
                Check(WalkableLine.CanTraverse(position,next,0.2f,terrain),"real-followed-detour-footprint "+speed+"/"+dt);
                position=next;
            }
            Check(follower.Reason=="arrived","followed-detour-arrival");
        }
        var blockedGoal=new LocalRoute(new[]{Vector2.Zero,new Vector2(3,0)},Vector2.Zero,new Vector2(4.2f,0),0.2f,terrain);
        for(int i=0;i<10 && !blockedGoal.Done;i++) blockedGoal.Advance(64,100);
        Check(blockedGoal.Reason=="end-target-blocked","precise-target-rejection");
        var distant=new LocalRoute(new[]{new Vector2(129,0),new Vector2(130,0)},Vector2.Zero,new Vector2(130,0),0,p=>true);
        distant.Advance(64,100);
        Check(distant.Reason=="start-too-far","bounded-start-connector");
        Func<Vector2,bool> ring=p=> {float d=Math.Max(Math.Abs(p.X-10),Math.Abs(p.Y)); return d<1 || d>1.5f;};
        var sealedGoal=new LocalSearch(Vector2.Zero,new Vector2(10,0),0.1f,ring);
        for(int i=0;i<1000 && !sealedGoal.Done;i++) sealedGoal.Advance(64,100);
        Check(sealedGoal.Reason=="unreachable","sealed-goal-rejected");
        var invalid=new LocalSearch(new Vector2(float.NaN,0),Vector2.Zero,0,p=>true);
        Check(invalid.Reason=="invalid","invalid-local-coordinates");
        var reversed=new LocalRoute(new[]{new Vector2(30,0),new Vector2(10,0)},Vector2.Zero,new Vector2(40,0),0.2f,p=>true);
        for(int i=0;i<5 && !reversed.Done;i++) reversed.Advance(64,100);
        Check(reversed.Reason=="found" && reversed.Points[0]==Vector2.Zero && reversed.Points[^1]==new Vector2(40,0),"formerly-distant-start-now-connected");
        Func<Vector2,bool> endpointObstacles=p=>!((Math.Abs(p.X-1)<0.25f || Math.Abs(p.X-9)<0.25f) && Math.Abs(p.Y)<0.5f);
        var edgePoints=new LocalRoute(new[]{new Vector2(1,0),new Vector2(3,0),new Vector2(7,0),new Vector2(9,0)},Vector2.Zero,new Vector2(11,0),0.2f,endpointObstacles);
        for(int i=0;i<1000 && !edgePoints.Done;i++) edgePoints.Advance(64,100);
        Check(edgePoints.Reason=="found" && edgePoints.TrimmedStartPoints==1 && edgePoints.TrimmedEndPoints==1,"blocked-road-endpoints-reconnected");
        for(int i=1;i<edgePoints.Points.Count;i++) Check(WalkableLine.CanTraverse(edgePoints.Points[i-1],edgePoints.Points[i],0.2f,endpointObstacles),"trimmed-endpoint-edges-clear");
        var escape=new LocalSearch(Vector2.Zero,new Vector2(3,1),0.2f,p=>p.Y>=-0.1f);
        for(int i=0;i<100 && !escape.Done;i++) escape.Advance(64,100);
        Check(escape.Reason=="found" && escape.Points[0]==Vector2.Zero,"leave-existing-footprint-overlap");
        Check(Vector2.Distance(escape.Points[0],escape.Points[1])<=1.5f,"escape-distance-bounded");
        for(int i=1;i<escape.Points.Count;i++) Check(WalkableLine.CanTraverse(escape.Points[i-1],escape.Points[i],0,p=>p.Y>=-0.1f),"escape-never-crosses-blocked-centre");
        var blockedCentre=new LocalSearch(Vector2.Zero,new Vector2(3,1),0.2f,p=>p.X>0.1f);
        blockedCentre.Advance(64,100);
        Check(blockedCentre.Reason=="source-blocked","escape-cannot-cross-wall-from-blocked-centre");
        var narrowSource=new LocalSearch(Vector2.Zero,new Vector2(3,0),0.5f,p=>p.X>2 || Math.Abs(p.Y)<0.1f);
        narrowSource.Advance(64,100);
        Check(narrowSource.Reason=="source-blocked","escape-cannot-cross-long-narrow-corridor");
    }

    private static void TestStraightFlight()
    {
        foreach(float speed in new[]{11f,33f})
        foreach(float dt in new[]{0.007f,0.016f,0.05f,0.25f})
        foreach(var goal in new[]{new Vector2(0.6f,0.8f),new Vector2(168,53),new Vector2(300,400)})
        {
            var follower=new PathFollower(); var position=Vector2.Zero;
            float length=goal.Length(), remaining=length, travelled=0;
            Check(follower.Start(new[]{position,goal},position,goal),"straight-flight-start-over-ground-connector-limit");
            for(int i=0;i<100000 && follower.Active;i++)
            {
                var movement=follower.Tick(position,dt,speed);
                Check(movement.Length()<=1.0001f,"flight-normal-input-magnitude");
                var next=position+movement*speed*dt;
                float distance=Vector2.Distance(next,goal);
                Check(distance<=remaining+0.001f,"flight-distance-decreases-without-overshoot");
                Check(Math.Abs(goal.X*next.Y-goal.Y*next.X)/length<0.05f,"flight-stays-on-straight-line");
                travelled+=Vector2.Distance(position,next); remaining=distance; position=next;
            }
            Check(follower.Reason=="arrived" && remaining<=0.8f,"straight-flight-arrives");
            Check(travelled<=length+0.1f,"flight-does-not-follow-road-detour");
            Check(follower.Tick(position,dt,speed)==Vector2.Zero,"flight-releases-horizontal-input-on-arrival");
        }
    }

    private static void TestObservedFlight()
    {
        foreach(float actualSpeed in new[]{11f,33f,66f})
        foreach(float frameDt in new[]{0.007f,0.016f,0.05f,0.25f})
        {
            var motion=new FlightMotion(); var follower=new PathFollower();
            var position=Vector2.Zero; var goal=new Vector2(120,160);
            double time=0; float serverClock=0,previousInput=0;
            const float serverDt=1f/30;
            motion.Reset(position,0,12);
            follower.Start(new[]{position,goal},position,goal);
            for(int i=0;i<50000 && follower.Active;i++)
            {
                time+=frameDt; motion.Sample(position,time,previousInput);
                var input=follower.Tick(position,frameDt,motion.Speed);
                input=motion.LimitNearGoal(input,Vector2.Distance(position,goal),frameDt);
                previousInput=input.Length(); serverClock+=frameDt;
                while(serverClock>=serverDt)
                {
                    var next=position+input*actualSpeed*serverDt;
                    Check(Vector2.Distance(next,goal)<=Vector2.Distance(position,goal)+0.001f,"server-tick-flight-does-not-overshoot");
                    position=next; serverClock-=serverDt;
                }
                Check(Math.Abs(goal.X*position.Y-goal.Y*position.X)/goal.Length()<0.05f,"observed-flight-keeps-straight-line");
            }
            Check(follower.Reason=="arrived","zero-native-speed-flight-arrives");
            Check(Math.Abs(motion.Speed-actualSpeed)<actualSpeed*0.2f,"speed-estimated-from-discrete-server-motion");
        }
        var noMotion=new FlightMotion(); noMotion.Reset(Vector2.Zero,0,0);
        noMotion.Sample(Vector2.Zero,0.2,1);
        Check(float.IsFinite(noMotion.Speed) && noMotion.Speed>0,"missing-client-speed-has-positive-fallback");
        noMotion.Sample(new Vector2(100,0),0.4,0);
        Check(noMotion.Speed==1,"manual-input-does-not-pollute-flight-speed");
    }
}


