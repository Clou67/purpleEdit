using System;
using System.Collections.Generic;
using System.Numerics;
using Mk2.Formats;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering
{
    /// <summary>The model being animated (or skeleton being inspected) in the Animations / Skeletons tabs.</summary>
    public sealed class AnimationState
    {
        public DrawItem Item;            // null = skeleton without a model
        public Animation Anim;
        public Matrix4x4[] Globals;      // model-space bone matrices of the current pose
        public Matrix4x4[] Skin;
        public float Frame = -1;         // < 0 = bind pose
        public int SelectedBone = -1;
        public bool HasPose;

        public Vector3 BoneWorld(int i) =>
            Vector3.Transform(Globals[i].Translation, Item?.World ?? Matrix4x4.Identity);
    }

    public sealed partial class SceneRenderer
    {
        public AnimationState Anim { get; private set; }
        private GpuLines _skeletonLines;
        private bool _skeletonDirty;

        /// <summary>
        /// Starts animating <paramref name="item"/> (null to show the skeleton alone). The item's model is
        /// rebuilt with skin data. Call with the GL context current.
        /// </summary>
        public void SetAnimation(DrawItem item, Animation anim)
        {
            ClearAnimation();
            if (item != null && (anim != null || item.Model.Animatable))
            {
                var loadedModel = item.Model.Source;
                var previous = item.Model;
                var rebuilt = GpuModel.Build(loadedModel, _textures, anim);
                foreach (var drawItem in Items) if (drawItem.Model == previous) drawItem.Model = rebuilt;
                _models[loadedModel] = rebuilt;
                _pickTris.Remove(previous);
                previous.Dispose();
                TransformBounds(rebuilt.Min, rebuilt.Max, item.World, out item.WorldMin, out item.WorldMax);
            }
            if (anim == null) return;
            Anim = new AnimationState
            {
                Item = item,
                Anim = anim,
                Globals = new Matrix4x4[anim.Bones.Count],
                Skin = new Matrix4x4[anim.Bones.Count],
            };
            SetPose(-1);
            if (item == null)
            {
                // Skeleton-only scene: frame the joints.
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                for (int i = 0; i < anim.Bones.Count; i++)
                {
                    min = Vector3.Min(min, Anim.Globals[i].Translation);
                    max = Vector3.Max(max, Anim.Globals[i].Translation);
                }
                if (min.X <= max.X)
                {
                    var pad = Vector3.Max((max - min) * 0.1f, new Vector3(10));
                    SceneMin = min - pad; SceneMax = max + pad;
                }
            }
        }

        public void ClearAnimation()
        {
            Anim = null;
            _skeletonDirty = true;
        }

        /// <summary>Poses the animated model at a frame (fractional frames interpolate; &lt; 0 = bind pose).</summary>
        public void SetPose(float frame)
        {
            var a = Anim;
            if (a == null) return;
            if (a.HasPose && a.Frame == frame) return;
            a.Anim.ComputePose(frame, a.Globals);
            a.Anim.SkinMatrices(a.Globals, a.Skin);
            a.Frame = frame;
            a.HasPose = true;
            if (a.Item != null)
            {
                a.Item.Model.ApplySkin(a.Skin);
                TransformBounds(a.Item.Model.Min, a.Item.Model.Max, a.Item.World, out a.Item.WorldMin, out a.Item.WorldMax);
            }
            _skeletonDirty = true;
        }

        public void SetSelectedBone(int bone)
        {
            if (Anim == null) return;
            Anim.SelectedBone = bone;
            _skeletonDirty = true;
        }

        private static readonly Vector4 BoneColor = new Vector4(0.2f, 0.95f, 1f, 1f);
        private static readonly Vector4 StaticBoneColor = new Vector4(0.45f, 0.6f, 0.75f, 1f);
        private static readonly Vector4 SelectedBoneColor = new Vector4(1f, 0.85f, 0.1f, 1f);
        private static readonly Vector4 ChildOfSelectedColor = new Vector4(1f, 0.5f, 0.1f, 1f);

        private void BuildSkeletonLines()
        {
            var d = new List<float>();
            var a = Anim;
            if (a != null && a.HasPose)
            {
                var bones = a.Anim.Bones;
                // Joint marker size from the overall skeleton size.
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                for (int i = 0; i < bones.Count; i++)
                {
                    min = Vector3.Min(min, a.Globals[i].Translation);
                    max = Vector3.Max(max, a.Globals[i].Translation);
                }
                float size = min.X <= max.X ? Math.Clamp((max - min).Length() * 0.012f, 0.5f, 40f) : 2f;
                for (int i = 0; i < bones.Count; i++)
                {
                    var b = bones[i];
                    var p = a.Globals[i].Translation;
                    bool sel = i == a.SelectedBone;
                    bool childOfSel = b.Parent >= 0 && b.Parent == a.SelectedBone;
                    var col = sel ? SelectedBoneColor : b.IsAnimated ? BoneColor : StaticBoneColor;
                    if (b.Parent >= 0)
                    {
                        var q = a.Globals[b.Parent].Translation;
                        if ((p - q).LengthSquared() > 1e-6f) Line(d, q, p, sel ? SelectedBoneColor : childOfSel ? ChildOfSelectedColor : col);
                    }
                    Cross(d, p, sel ? size * 2.5f : size, col);
                    if (sel)
                    {
                        // Local axes of the selected bone: X red, Y green, Z blue.
                        var m = a.Globals[i];
                        float ax = size * 6f;
                        Line(d, p, p + Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13)) * ax, new Vector4(1, 0.2f, 0.2f, 1));
                        Line(d, p, p + Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23)) * ax, new Vector4(0.2f, 1, 0.2f, 1));
                        Line(d, p, p + Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33)) * ax, new Vector4(0.3f, 0.5f, 1, 1));
                    }
                }
            }
            if (_skeletonLines == null) _skeletonLines = GpuLines.Create(d, dynamic: true);
            else _skeletonLines.Update(d);
            _skeletonDirty = false;
        }

        private void DrawSkeleton(ViewSettings s)
        {
            if (Anim == null || !s.Skeleton) return;
            if (_skeletonDirty || _skeletonLines == null) BuildSkeletonLines();
            _lines.Use();
            _lines.Set("uModel", Anim.Item?.World ?? Matrix4x4.Identity);
            _lines.Set("uTint", Vector4.One);
            GL.Disable(EnableCap.DepthTest);
            _skeletonLines.Draw();
            GL.Enable(EnableCap.DepthTest);
            _lines.Set("uModel", Matrix4x4.Identity);
        }
    }
}
