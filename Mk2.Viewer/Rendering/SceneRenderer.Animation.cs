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

        public Vector3 BoneWorld(int bone) =>
            Vector3.Transform(Globals[bone].Translation, Item?.World ?? Matrix4x4.Identity);
    }

    public sealed partial class SceneRenderer
    {
        public AnimationState Anim { get; private set; }
        private GpuLines skeletonLines;
        private bool skeletonDirty;

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
                var rebuilt = GpuModel.Build(loadedModel, textures, anim);
                foreach (var drawItem in Items) if (drawItem.Model == previous) drawItem.Model = rebuilt;
                models[loadedModel] = rebuilt;
                pickTriangles.Remove(previous);
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
                for (int index = 0; index < anim.Bones.Count; index++)
                {
                    min = Vector3.Min(min, Anim.Globals[index].Translation);
                    max = Vector3.Max(max, Anim.Globals[index].Translation);
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
            skeletonDirty = true;
        }

        /// <summary>Poses the animated model at a frame (fractional frames interpolate; &lt; 0 = bind pose).</summary>
        public void SetPose(float frame)
        {
            var state = Anim;
            if (state == null) return;
            if (state.HasPose && state.Frame == frame) return;
            state.Anim.ComputePose(frame, state.Globals);
            state.Anim.SkinMatrices(state.Globals, state.Skin);
            state.Frame = frame;
            state.HasPose = true;
            if (state.Item != null)
            {
                state.Item.Model.ApplySkin(state.Skin);
                TransformBounds(state.Item.Model.Min, state.Item.Model.Max, state.Item.World, out state.Item.WorldMin, out state.Item.WorldMax);
            }
            skeletonDirty = true;
        }

        public void SetSelectedBone(int bone)
        {
            if (Anim == null) return;
            Anim.SelectedBone = bone;
            skeletonDirty = true;
        }

        private static readonly Vector4 BoneColor = new Vector4(0.2f, 0.95f, 1f, 1f);
        private static readonly Vector4 StaticBoneColor = new Vector4(0.45f, 0.6f, 0.75f, 1f);
        private static readonly Vector4 SelectedBoneColor = new Vector4(1f, 0.85f, 0.1f, 1f);
        private static readonly Vector4 ChildOfSelectedColor = new Vector4(1f, 0.5f, 0.1f, 1f);

        private void BuildSkeletonLines()
        {
            var lineData = new List<float>();
            var state = Anim;
            if (state != null && state.HasPose)
            {
                var bones = state.Anim.Bones;
                // Joint marker size from the overall skeleton size.
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                for (int bone = 0; bone < bones.Count; bone++)
                {
                    min = Vector3.Min(min, state.Globals[bone].Translation);
                    max = Vector3.Max(max, state.Globals[bone].Translation);
                }
                float size = min.X <= max.X ? Math.Clamp((max - min).Length() * 0.012f, 0.5f, 40f) : 2f;
                for (int index = 0; index < bones.Count; index++)
                {
                    var bone = bones[index];
                    var position = state.Globals[index].Translation;
                    bool selected = index == state.SelectedBone;
                    bool childOfSel = bone.Parent >= 0 && bone.Parent == state.SelectedBone;
                    var color = selected ? SelectedBoneColor : bone.IsAnimated ? BoneColor : StaticBoneColor;
                    if (bone.Parent >= 0)
                    {
                        var parentPosition = state.Globals[bone.Parent].Translation;
                        if ((position - parentPosition).LengthSquared() > 1e-6f) Line(lineData, parentPosition, position, selected ? SelectedBoneColor : childOfSel ? ChildOfSelectedColor : color);
                    }
                    Cross(lineData, position, selected ? size * 2.5f : size, color);
                    if (selected)
                    {
                        // Local axes of the selected bone: X red, Y green, Z blue.
                        var global = state.Globals[index];
                        float axisLength = size * 6f;
                        Line(lineData, position, position + Vector3.Normalize(new Vector3(global.M11, global.M12, global.M13)) * axisLength, new Vector4(1, 0.2f, 0.2f, 1));
                        Line(lineData, position, position + Vector3.Normalize(new Vector3(global.M21, global.M22, global.M23)) * axisLength, new Vector4(0.2f, 1, 0.2f, 1));
                        Line(lineData, position, position + Vector3.Normalize(new Vector3(global.M31, global.M32, global.M33)) * axisLength, new Vector4(0.3f, 0.5f, 1, 1));
                    }
                }
            }
            if (skeletonLines == null) skeletonLines = GpuLines.Create(lineData, dynamic: true);
            else skeletonLines.Update(lineData);
            skeletonDirty = false;
        }

        private void DrawSkeleton(ViewSettings settings)
        {
            if (Anim == null || !settings.Skeleton) return;
            if (skeletonDirty || skeletonLines == null) BuildSkeletonLines();
            lineShader.Use();
            lineShader.Set("uModel", Anim.Item?.World ?? Matrix4x4.Identity);
            lineShader.Set("uTint", Vector4.One);
            GL.Disable(EnableCap.DepthTest);
            skeletonLines.Draw();
            GL.Enable(EnableCap.DepthTest);
            lineShader.Set("uModel", Matrix4x4.Identity);
        }
    }
}
