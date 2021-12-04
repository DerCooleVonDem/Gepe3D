
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Desktop;

namespace Gepe3D
{
    public class MainWindow : GameWindow
    {
        
        static void Main(string[] args)
        {
            int WIDTH = 1600;
            int HEIGHT = 900;
            string TITLE = "Gepe3D";
            
            GameWindowSettings settings = GameWindowSettings.Default;
            settings.UpdateFrequency = 100;
            
            new MainWindow(
                settings,
                new NativeWindowSettings()
                {
                    Size = new Vector2i(WIDTH, HEIGHT),
                    Title = TITLE,
                    // WindowBorder = WindowBorder.Hidden
                }
            ).Run();
        }
        
        public Scene ActiveScene;
        
        public MainWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
            : base(gameWindowSettings, nativeWindowSettings)
        {
        }
        
        protected override void OnLoad()
        {
            base.OnLoad();
            GL.ClearColor(0, 0, 0, 1);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.Blend);
            
            CursorGrabbed = true;
            Global.keyboardState = KeyboardState;
            Global.mouseState = MouseState;
            
            this.ActiveScene = new World(this);
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            if (Global.IsKeyDown(Keys.Escape)) { Close(); }

            Global.keyboardState = KeyboardState;
            Global.mouseState = MouseState;
                
            ActiveScene.Update();
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            ActiveScene.Render();
            
            SwapBuffers();
        }
        
        public void SwitchToScene(Scene scene)
        {
            this.ActiveScene = scene;
        }
        
    }
}