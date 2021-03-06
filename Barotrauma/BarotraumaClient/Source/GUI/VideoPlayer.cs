using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Xml.Linq;
using Barotrauma.Media;
using System.IO;
using Microsoft.Xna.Framework.Input;

namespace Barotrauma
{
    class VideoPlayer
    {
        private Video currentVideo;
        private string filePath;
        private bool isPlaying;

        private GUIFrame background, videoFrame, textFrame;
        private GUITextBlock title, textContent, objectiveTitle, objectiveText;
        private GUICustomComponent videoView;
        private GUIButton okButton;

        private Color backgroundColor = new Color(0f, 0f, 0f, 1f);
        private Action callbackOnStop;

        private Point scaledVideoResolution;
        private readonly int borderSize = 20;
        private readonly Point buttonSize = new Point(160, 50);
        private readonly int titleHeight = 30;
        private readonly int objectiveFrameHeight = 60;
        private readonly int textHeight = 25;

        public struct TextSettings
        {
            public string Text;
            public int Width;

            public TextSettings(XElement element)
            {
                Text = TextManager.GetFormatted(element.GetAttributeString("text", string.Empty), true);
                Width = element.GetAttributeInt("width", 450);
            }
        }

        public struct VideoSettings
        {
            public string File;

            public VideoSettings(XElement element)
            {
                File = element.GetAttributeString("file", string.Empty);
            }
        }

        public VideoPlayer() // GUI elements with size set to Point.Zero are resized based on content
        {
            int screenWidth = (int)(GameMain.GraphicsWidth * 0.55f);
            scaledVideoResolution = new Point(screenWidth, (int)(screenWidth / 16f * 9f));

            int width = scaledVideoResolution.X;
            int height = scaledVideoResolution.Y;

            background = new GUIFrame(new RectTransform(Point.Zero, GUI.Canvas, Anchor.Center), "InnerFrame", backgroundColor);
            videoFrame = new GUIFrame(new RectTransform(Point.Zero, background.RectTransform, Anchor.Center, Pivot.Center), "SonarFrame");

            textFrame = new GUIFrame(new RectTransform(Point.Zero, videoFrame.RectTransform, Anchor.CenterLeft, Pivot.CenterLeft), "TextFrame");

            videoView = new GUICustomComponent(new RectTransform(Point.Zero, videoFrame.RectTransform, Anchor.Center), (spriteBatch, guiCustomComponent) => { DrawVideo(spriteBatch, guiCustomComponent.Rect); });
            title = new GUITextBlock(new RectTransform(Point.Zero, textFrame.RectTransform, Anchor.TopLeft, Pivot.TopLeft), string.Empty, font: GUI.VideoTitleFont, textColor: new Color(253, 174, 0), textAlignment: Alignment.Left);

            textContent = new GUITextBlock(new RectTransform(Point.Zero, textFrame.RectTransform, Anchor.TopLeft, Pivot.TopLeft), string.Empty, font: GUI.Font, textAlignment: Alignment.TopLeft);

            objectiveTitle = new GUITextBlock(new RectTransform(new Vector2(1f, 0f), textFrame.RectTransform, Anchor.TopCenter, Pivot.TopCenter), string.Empty, font: GUI.ObjectiveTitleFont, textAlignment: Alignment.CenterRight, textColor: Color.White);
            objectiveTitle.Text = TextManager.Get("NewObjective");
            objectiveText = new GUITextBlock(new RectTransform(Point.Zero, textFrame.RectTransform, Anchor.TopCenter, Pivot.TopCenter), string.Empty, font: GUI.ObjectiveNameFont, textColor: new Color(4, 180, 108), textAlignment: Alignment.CenterRight);

            objectiveTitle.Visible = objectiveText.Visible = false;
        }

        public void Play()
        {
            isPlaying = true;
        }

        public void Stop()
        {
            isPlaying = false;
            if (currentVideo == null) return;
            currentVideo.Dispose();
            currentVideo = null;
        }

        private bool DisposeVideo(GUIButton button, object userData)
        {
            Stop();
            callbackOnStop?.Invoke();
            return true;
        }

        public void Update()
        {
            if (currentVideo == null) return;

            if (PlayerInput.KeyHit(Keys.Enter) || PlayerInput.KeyHit(Keys.Escape))
            {
                DisposeVideo(null, null);
                return;
            }

            if (currentVideo.IsPlaying) return;

            currentVideo.Dispose();
            currentVideo = null;
            currentVideo = CreateVideo(scaledVideoResolution);
        }

        public void AddToGUIUpdateList(bool ignoreChildren = false, int order = 0)
        {
            if (!isPlaying) return;
            background.AddToGUIUpdateList(ignoreChildren, order);
        }

        public void LoadContent(string contentPath, VideoSettings videoSettings, TextSettings textSettings, string contentId, bool startPlayback, string objective = "", Action callback = null)
        {
            callbackOnStop = callback;
            filePath = contentPath + videoSettings.File;

            if (!File.Exists(filePath))
            {
                DebugConsole.ThrowError("No video found at: " + filePath);
                DisposeVideo(null, null);
                return;
            }

            if (currentVideo != null)
            {
                currentVideo.Dispose();
                currentVideo = null;
            }

            currentVideo = CreateVideo(scaledVideoResolution);
            title.Text = TextManager.Get(contentId);
            textContent.Text = textSettings.Text;
            objectiveText.Text = objective;

            AdjustFrames(videoSettings, textSettings);

            if (startPlayback) Play();
        }

        private void AdjustFrames(VideoSettings videoSettings, TextSettings textSettings)
        {
            int screenWidth = (int)(GameMain.GraphicsWidth * 0.55f);
            scaledVideoResolution = new Point(screenWidth, (int)(screenWidth / 16f * 9f));

            background.RectTransform.NonScaledSize = Point.Zero;
            videoFrame.RectTransform.NonScaledSize = Point.Zero;
            videoView.RectTransform.NonScaledSize = Point.Zero;

            title.RectTransform.NonScaledSize = Point.Zero;
            textFrame.RectTransform.NonScaledSize = Point.Zero;
            textContent.RectTransform.NonScaledSize = Point.Zero;

            objectiveText.RectTransform.NonScaledSize = Point.Zero;

            title.TextScale = textContent.TextScale = objectiveText.TextScale = objectiveTitle.TextScale = GUI.Scale;

            int scaledBorderSize = (int)(borderSize * GUI.Scale);
            int scaledTextWidth = (int)(textSettings.Width * GUI.Scale);
            int scaledTitleHeight = (int)(titleHeight * GUI.Scale);
            int scaledTextHeight = (int)(textHeight * GUI.Scale);
            int scaledObjectiveFrameHeight = (int)(objectiveFrameHeight * GUI.Scale);

            Point scaledButtonSize = new Point((int)(buttonSize.X * GUI.Scale), (int)(buttonSize.Y * GUI.Scale));

            background.RectTransform.NonScaledSize = new Point(GameMain.GraphicsWidth, GameMain.GraphicsHeight);

            videoFrame.RectTransform.NonScaledSize += scaledVideoResolution + new Point(scaledBorderSize, scaledBorderSize);
            videoView.RectTransform.NonScaledSize += scaledVideoResolution;

            title.RectTransform.NonScaledSize += new Point(scaledTextWidth, scaledTitleHeight);
            title.RectTransform.AbsoluteOffset = new Point((int)(5 * GUI.Scale), (int)(10 * GUI.Scale));

            if (!string.IsNullOrEmpty(textSettings.Text))
            {
                textSettings.Text = ToolBox.WrapText(textSettings.Text, scaledTextWidth, GUI.Font);
                int wrappedHeight = textSettings.Text.Split('\n').Length * scaledTextHeight;

                textFrame.RectTransform.NonScaledSize += new Point(scaledTextWidth + scaledBorderSize, wrappedHeight + scaledBorderSize + scaledButtonSize.Y + scaledTitleHeight);
                textFrame.RectTransform.AbsoluteOffset = new Point(scaledVideoResolution.X + scaledBorderSize * 2, 0);

                textContent.RectTransform.NonScaledSize += new Point(scaledTextWidth, wrappedHeight);
                textContent.RectTransform.AbsoluteOffset = new Point(0, scaledBorderSize + scaledTitleHeight);
            }

            if (!string.IsNullOrEmpty(objectiveText.Text))
            {
                int scaledXOffset = (int)(-10 * GUI.Scale);

                objectiveTitle.RectTransform.AbsoluteOffset = new Point(scaledXOffset, textContent.RectTransform.Rect.Height + (int)(scaledTextHeight * 1.95f));
                objectiveText.RectTransform.AbsoluteOffset = new Point(scaledXOffset, textContent.RectTransform.Rect.Height + objectiveTitle.Rect.Height + (int)(scaledTextHeight * 2.25f));

                textFrame.RectTransform.NonScaledSize += new Point(0, scaledObjectiveFrameHeight);
                objectiveText.RectTransform.NonScaledSize += new Point(textFrame.Rect.Width, scaledTextHeight);
                objectiveTitle.Visible = objectiveText.Visible = true;
            }
            else
            {
                textFrame.RectTransform.NonScaledSize += new Point(0, scaledBorderSize);
                objectiveTitle.Visible = objectiveText.Visible = false;
            }

            int totalFrameWidth = videoFrame.Rect.Width + textFrame.Rect.Width + scaledBorderSize * 2;
            int xOffset = videoFrame.Rect.Width / 2 + scaledBorderSize - (videoFrame.Rect.Width / 2 - textFrame.Rect.Width / 2);


            videoFrame.RectTransform.AbsoluteOffset = new Point(-xOffset, (int)(50 * GUI.Scale));

            if (okButton != null)
            {
                textFrame.RemoveChild(okButton);
                okButton = null;
            }

            okButton = new GUIButton(new RectTransform(scaledButtonSize, textFrame.RectTransform, Anchor.BottomRight, Pivot.BottomRight) { AbsoluteOffset = new Point(scaledBorderSize, scaledBorderSize) }, TextManager.Get("OK"))
            {
                OnClicked = DisposeVideo
            };
        }

        private Video CreateVideo(Point resolution)
        {
            Video video = null;

            try
            {
                video = new Video(GameMain.Instance.GraphicsDevice, GameMain.SoundManager, filePath, (uint)resolution.X, (uint)resolution.Y);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Error loading video content " + filePath + "!", e);
            }

            return video;
        }

        private void DrawVideo(SpriteBatch spriteBatch, Rectangle rect)
        {
            if (!isPlaying) return;
            spriteBatch.Draw(currentVideo.GetTexture(), rect, Color.White);
        }

        public void Remove()
        {
            if (currentVideo != null)
            {
                currentVideo.Dispose();
                currentVideo = null;
            }
        }
    }
}
