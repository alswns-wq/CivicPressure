using System;
using UnityEngine;

namespace CivicPressure
{
    // Render-only smoothing. Never predicts beyond a published simulation state.
    internal sealed class DevVisualMotion
    {
        private bool initialized;
        private double sample, value;
        internal void Reset() { initialized = false; }
        internal double Follow(double target, float delta, bool paused)
        {
            if (double.IsNaN(target) || double.IsInfinity(target)) return value;
            if (!initialized || target < sample) { initialized = true; sample = value = target; return value; }
            sample = target;
            if (!paused) value += (target - value) * Blend(delta, 25);
            return value;
        }
        internal static float Blend(float delta, float response)
        { return float.IsNaN(delta) || float.IsInfinity(delta) || delta <= 0 ? 0 : (float)(1 - Math.Exp(-Math.Min(.2f, delta) * response)); }
        internal static float FrameDelta(float delta, bool paused)
        { return paused || float.IsNaN(delta) || float.IsInfinity(delta) ? 0 : Math.Max(0, Math.Min(.2f, delta)); }
        internal static Vector3 LookDirection(Vector3 travel,bool moving,Vector3 aim)
        {
            travel.y=aim.y=0;
            // A normal per-frame step is only millimetres at high FPS. Normalize before
            // rendering so a world-distance cutoff cannot discard its heading.
            return (moving && travel.sqrMagnitude>.00000001f ? travel : aim).normalized;
        }
        internal static float Interval(float distanceSquared) { return distanceSquared <= 250 * 250 ? 0 : 1f / 15f; }
        internal static float BoneInterval(float distanceSquared)
        { return distanceSquared<=60*60 ? 1f/20 : distanceSquared<=250*250 ? 1f/12 : 1f/5; }
        internal static float NextBone(float now,float deadline,float interval)
        {
            float next=deadline+(float)(Math.Floor(Math.Max(0,now-deadline)/interval)+1)*interval;
            return next>now ? next : now+interval; // Float rounding at an exact tick must not replay it.
        }
        // Presentation only: personal pauses, wind-up and follow-through. No allocations or simulation writes.
        internal static float Gesture(int index,float time)
        {
            float period=8+index%5, phase=(time+index*2.399963f)%period;
            if(phase<0) phase+=period;
            if(phase>=2.6f) return 0;
            float wave=(float)Math.Sin(phase/2.6f*Math.PI);
            return wave*wave;
        }
        internal static float Strike(float age,float duration)
        {
            if(duration<=0 || age<=0 || age>=duration) return 0;
            float t=age/duration;
            if(t<.25f) return -.3f*Smooth(t/.25f);
            if(t<.5f) return -.3f+1.3f*Smooth((t-.25f)/.25f);
            return 1-Smooth((t-.5f)/.5f);
        }
        private static float Smooth(float t) { return t*t*(3-2*t); }
        internal static float ThrowAngle(float progress)
        {
            float t=Math.Max(0,Math.Min(1,progress));
            if(t<.3f) return 35-165*Smooth(t/.3f);
            if(t<.6f) return -130+170*Smooth((t-.3f)/.3f);
            return 40-5*Smooth((t-.6f)/.4f);
        }
        internal static float GripPulse(int index,float time)
        {
            if(time<=2 || time>=8) return 0;
            float fade=Math.Min(1,Math.Min((time-2)*3,(8-time)*3));
            return fade*(float)Math.Sin((time-2)*Math.PI*8/6+index%3*.3f);
        }
    }
}
