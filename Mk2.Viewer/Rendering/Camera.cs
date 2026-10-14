using System;
using System.Numerics;

namespace Mk2.Viewer.Rendering
{
    /// <summary>Free-fly camera. +Y is up (the game's convention).</summary>
    public sealed class Camera
    {
        public Vector3 Position = new Vector3(0, 1000, 3000);
        public float Yaw;            // radians, 0 = looking towards -Z
        public float Pitch;          // radians, positive = up
        public float FovY = 60f * MathF.PI / 180f;
        public float Near = 20f;
        public float Far = 600000f;
        public float Speed = 3000f;  // units per second

        public Vector3 Forward => new Vector3(
            MathF.Cos(Pitch) * MathF.Sin(Yaw),
            MathF.Sin(Pitch),
            -MathF.Cos(Pitch) * MathF.Cos(Yaw));

        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        public Vector3 Up => Vector3.Cross(Right, Forward);

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

        public Matrix4x4 Projection(float aspect) => GlUtil.Perspective(FovY, aspect, Near, Far);

        public void LookAt(Vector3 eye, Vector3 target)
        {
            Position = eye;
            var d = target - eye;
            if (d.LengthSquared() < 1e-6f) return;
            d = Vector3.Normalize(d);
            Pitch = MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
            Yaw = MathF.Atan2(d.X, -d.Z);
        }

        /// <summary>
        /// Frame an axis-aligned box from a pleasant angle. <paramref name="fromFront"/> views it from the −Z side:
        /// characters and animated props face −Z, so the default (+Z) angle would show their backs.
        /// </summary>
        public void Frame(Vector3 min, Vector3 max, bool fromFront = false)
        {
            var c = (min + max) * 0.5f;
            float r = Math.Max((max - min).Length() * 0.5f, 100f);
            var dir = Vector3.Normalize(fromFront ? new Vector3(0.55f, 0.35f, -0.75f) : new Vector3(0.6f, 0.55f, 0.6f));
            LookAt(c + dir * r * 1.6f, c);
            Speed = Math.Clamp(r * 0.6f, 500f, 40000f);
            Far = Math.Max(600000f, r * 8f);
        }

        public void Rotate(float dx, float dy)
        {
            Yaw += dx;
            Pitch = Math.Clamp(Pitch + dy, -1.55f, 1.55f);
        }

        /// <summary>World-space ray through a pixel.</summary>
        public void Ray(float px, float py, int w, int h, out Vector3 origin, out Vector3 dir)
        {
            float x = 2f * px / Math.Max(w, 1) - 1f;
            float y = 1f - 2f * py / Math.Max(h, 1);
            float t = MathF.Tan(FovY / 2f);
            float aspect = w / (float)Math.Max(h, 1);
            var f = Forward; var r = Right; var u = Up;
            origin = Position;
            dir = Vector3.Normalize(f + r * (x * t * aspect) + u * (y * t));
        }
    }
}
