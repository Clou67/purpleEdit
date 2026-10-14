using System;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering {
    public sealed class RenderTarget : IDisposable {
        public int Fbo { get; private set; }
        public int ColorTexture { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        private int _depthRbo;
        private bool _created;

        public void RenderToTexture(int width, int height, Action renderFunction) {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            if (!_created)
                Create(width, height);
            else if (width != Width || height != Height)
                Resize(width, height);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.Viewport(0, 0, Width, Height);

            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);

            int vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);

            renderFunction();

            GL.BindVertexArray(0);
            GL.DeleteVertexArray(vao);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void Create(int width, int height) {
            Width = width;
            Height = height;

            Fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);

            ColorTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, ColorTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, ColorTexture, 0);

            _depthRbo = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, width, height);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depthRbo);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("RenderTarget framebuffer incomplete: " + status);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _created = true;
        }

        private void Resize(int width, int height) {
            Width = width;
            Height = height;

            GL.BindTexture(TextureTarget.Texture2D, ColorTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, width, height);
        }

        public void Dispose() {
            if (!_created) return;
            GL.DeleteFramebuffer(Fbo);
            GL.DeleteTexture(ColorTexture);
            GL.DeleteRenderbuffer(_depthRbo);
            _created = false;
        }
    }
}