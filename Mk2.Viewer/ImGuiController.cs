using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Mk2.Viewer.Rendering;

namespace Mk2.Viewer.ImGuiUi {
    public sealed class ImGuiController : IDisposable {
        private bool _frameBegun;

        private int _vao, _vbo, _ebo;
        private int _vboSize = 10000, _eboSize = 2000;

        private readonly Dictionary<int, int> _textures = new Dictionary<int, int>();
        private ShaderProgram _shader;

        private int _width, _height;

        public ImGuiController(int width, int height) {
            _width = width;
            _height = height;

            ImGuiContextPtr ctx = Hexa.NET.ImGui.ImGui.CreateContext();
            Hexa.NET.ImGui.ImGui.SetCurrentContext(ctx);
            var io = Hexa.NET.ImGui.ImGui.GetIO();
            unsafe { io.Fonts.AddFontDefault(); }

            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            unsafe { io.IniFilename = null; } // no imgui.ini

            CreateDeviceResources();

            SetPerFrameData(1f / 60f);

            Hexa.NET.ImGui.ImGui.NewFrame();
            _frameBegun = true;
        }

        public void WindowResized(int width, int height) {
            _width = width;
            _height = height;
        }

        private void CreateDeviceResources() {
            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);

            _vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, _vboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            _ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, _eboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            const string vs = @"#version 330 core
uniform mat4 uProjection;
layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;
out vec4 color;
out vec2 texCoord;
void main() {
    gl_Position = uProjection * vec4(in_position, 0, 1);
    color = in_color;
    texCoord = in_texCoord;
}";
            const string fs = @"#version 330 core
uniform sampler2D uFontTexture;
in vec4 color;
in vec2 texCoord;
out vec4 outputColor;
void main() { outputColor = color * texture(uFontTexture, texCoord); }";
            _shader = new ShaderProgram(vs, fs);

            SetVertexLayout();
        }

        private unsafe void SetVertexLayout() {
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);

            int stride = sizeof(ImDrawVert);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
        }

        private void UpdateTextures(ImDrawDataPtr drawData) {
            var list = drawData.Textures;
            for (int i = 0; i < list.Size; i++) {
                ImTextureDataPtr tex = list[i];
                if (tex.Status == ImTextureStatus.WantCreate) CreateTexture(tex);
                else if (tex.Status == ImTextureStatus.WantUpdates) { DestroyTexture(tex); CreateTexture(tex); }
                else if (tex.Status == ImTextureStatus.WantDestroy) DestroyTexture(tex);
            }
        }

        private unsafe void CreateTexture(ImTextureDataPtr tex) {
            int handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, tex.Width, tex.Height, 0,
                           OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, (IntPtr)tex.GetPixels());

            _textures[tex.UniqueID] = handle;
            tex.SetTexID((ulong)(uint)handle);
            tex.SetStatus(ImTextureStatus.Ok);
        }

        private void DestroyTexture(ImTextureDataPtr tex) {
            if (_textures.TryGetValue(tex.UniqueID, out int handle)) {
                GL.DeleteTexture(handle);
                _textures.Remove(tex.UniqueID);
            }
            tex.SetTexID((nint)0);
            tex.SetStatus(ImTextureStatus.Destroyed);
        }
        public void Render() {
            if (!_frameBegun) return;
            _frameBegun = false;

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            SetVertexLayout();

            Hexa.NET.ImGui.ImGui.Render();
            RenderDrawData(Hexa.NET.ImGui.ImGui.GetDrawData());
        }

        public void Update(GameWindow window, float deltaSeconds) {
            if (_frameBegun) Hexa.NET.ImGui.ImGui.Render();

            SetPerFrameData(deltaSeconds);
            UpdateInput(window);

            _frameBegun = true;
            Hexa.NET.ImGui.ImGui.NewFrame();
        }

        private void SetPerFrameData(float deltaSeconds) {
            var io = Hexa.NET.ImGui.ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(_width, _height);
            io.DisplayFramebufferScale = System.Numerics.Vector2.One;
            io.DeltaTime = deltaSeconds;
        }

        private readonly List<char> _pressedChars = new List<char>();

        internal void MouseScroll(OpenTK.Mathematics.Vector2 offset) {
            var io = Hexa.NET.ImGui.ImGui.GetIO();
            io.MouseWheel += offset.Y;
            io.MouseWheelH += offset.X;
        }

        public void PressChar(char c) => _pressedChars.Add(c);

        private static readonly (MouseButton Button, ImGuiMouseButton ImGui)[] MouseButtonMap = {
            (MouseButton.Left, ImGuiMouseButton.Left),
            (MouseButton.Right, ImGuiMouseButton.Right),
            (MouseButton.Middle, ImGuiMouseButton.Middle),
        };

        private void UpdateInput(GameWindow window) {
            var io = Hexa.NET.ImGui.ImGui.GetIO();

            var mouse = window.MouseState;
            io.MousePos = new System.Numerics.Vector2(mouse.X, mouse.Y);
            foreach (var (btn, imguiBtn) in MouseButtonMap)
                io.MouseDown[(int)imguiBtn] = window.IsFocused && mouse.IsButtonDown(btn);

            var keyboard = window.KeyboardState;
            if (window.IsFocused) {
                io.KeyCtrl = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
                io.KeyAlt = keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt);
                io.KeyShift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
                io.KeySuper = keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper);

                foreach (var kv in KeyMap)
                    io.AddKeyEvent(kv.Value, keyboard.IsKeyDown(kv.Key));
            }

            foreach (var c in _pressedChars) io.AddInputCharacter(c);
            _pressedChars.Clear();
        }

        private static readonly Dictionary<Keys, ImGuiKey> KeyMap = new Dictionary<Keys, ImGuiKey> {
            { Keys.Tab, ImGuiKey.Tab },
            { Keys.Left, ImGuiKey.LeftArrow },
            { Keys.Right, ImGuiKey.RightArrow },
            { Keys.Up, ImGuiKey.UpArrow },
            { Keys.Down, ImGuiKey.DownArrow },
            { Keys.PageUp, ImGuiKey.PageUp },
            { Keys.PageDown, ImGuiKey.PageDown },
            { Keys.Home, ImGuiKey.Home },
            { Keys.End, ImGuiKey.End },
            { Keys.Delete, ImGuiKey.Delete },
            { Keys.Backspace, ImGuiKey.Backspace },
            { Keys.Enter, ImGuiKey.Enter },
            { Keys.Escape, ImGuiKey.Escape },
            { Keys.Space, ImGuiKey.Space },
            { Keys.LeftControl, ImGuiKey.LeftCtrl },
            { Keys.RightControl, ImGuiKey.RightCtrl },
            { Keys.LeftShift, ImGuiKey.LeftShift },
            { Keys.RightShift, ImGuiKey.RightShift },
            { Keys.LeftAlt, ImGuiKey.LeftAlt },
            { Keys.RightAlt, ImGuiKey.RightAlt },
            { Keys.C, ImGuiKey.C },
            { Keys.V, ImGuiKey.V },
            { Keys.X, ImGuiKey.X },
            { Keys.Y, ImGuiKey.Y },
            { Keys.Z, ImGuiKey.Z },
            { Keys.A, ImGuiKey.A },
            { Keys.W, ImGuiKey.W },
            { Keys.S, ImGuiKey.S },
            { Keys.D, ImGuiKey.D },
            { Keys.Q, ImGuiKey.Q },
            { Keys.E, ImGuiKey.E },
            { Keys.F, ImGuiKey.F },
        };

        private unsafe void RenderDrawData(ImDrawDataPtr drawData) {
            if (_shader == null || drawData.CmdListsCount == 0) return;

            UpdateTextures(drawData);

            for (int i = 0; i < drawData.CmdListsCount; i++) {
                ImDrawListPtr cmdList = drawData.CmdLists[i];

                int vertexSize = cmdList.VtxBuffer.Size * sizeof(ImDrawVert);
                if (vertexSize > _vboSize) {
                    int newSize = (int)Math.Max(_vboSize * 1.5f, vertexSize);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                    GL.BufferData(BufferTarget.ArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    _vboSize = newSize;
                }

                int indexSize = cmdList.IdxBuffer.Size * sizeof(ushort);
                if (indexSize > _eboSize) {
                    int newSize = (int)Math.Max(_eboSize * 1.5f, indexSize);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    _eboSize = newSize;
                }
            }

            var io = Hexa.NET.ImGui.ImGui.GetIO();
            var proj = System.Numerics.Matrix4x4.CreateOrthographicOffCenter(0, io.DisplaySize.X, io.DisplaySize.Y, 0, -1f, 1f);

            _shader.Use();
            _shader.Set("uProjection", proj);
            _shader.Set("uFontTexture", 0);

            GL.BindVertexArray(_vao);

            drawData.ScaleClipRects(io.DisplayFramebufferScale);

            GL.Enable(EnableCap.Blend);
            GL.Enable(EnableCap.ScissorTest);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);

            for (int n = 0; n < drawData.CmdListsCount; n++) {
                ImDrawListPtr cmdList = drawData.CmdLists[n];

                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, cmdList.VtxBuffer.Size * sizeof(ImDrawVert), (IntPtr)cmdList.VtxBuffer.Data);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
                GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, cmdList.IdxBuffer.Size * sizeof(ushort), (IntPtr)cmdList.IdxBuffer.Data);

                int vtxOffset = 0, idxOffset = 0;
                for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++) {
                    ImDrawCmd cmd = cmdList.CmdBuffer[cmdI];
                    if (cmd.UserCallback != null) continue; // no user callbacks used here

                    ImTextureID texId = cmd.GetTexID();
                    int glHandle = (int)(ulong)texId;

                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, glHandle);

                    var clip = cmd.ClipRect;
                    GL.Scissor((int)clip.X, _height - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));

                    if ((io.BackendFlags & ImGuiBackendFlags.RendererHasVtxOffset) != 0)
                        GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)cmd.ElemCount, DrawElementsType.UnsignedShort, (IntPtr)(idxOffset * sizeof(ushort)), vtxOffset);
                    else
                        GL.DrawElements(BeginMode.Triangles, (int)cmd.ElemCount, DrawElementsType.UnsignedShort, (int)cmd.IdxOffset * sizeof(ushort));

                    idxOffset += (int)cmd.ElemCount;
                }
                vtxOffset += cmdList.VtxBuffer.Size;
            }

            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
        }

        public void Dispose() {
            foreach (var handle in _textures.Values) GL.DeleteTexture(handle);
            _textures.Clear();
            _shader?.Dispose();
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            if (_ebo != 0) GL.DeleteBuffer(_ebo);
        }
    }
}