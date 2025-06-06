#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Barotrauma
{
    internal partial class Radiation
    {
        private int? radiationMultiplier;
        private static float spriteIndex;
        private readonly SpriteSheet? radiationEdgeAnimSheet = GUIStyle.RadiationAnimSpriteSheet;
        private int maxFrames => (radiationEdgeAnimSheet?.FrameCount ?? 0) + 1;

        private bool isHoveringOver;

        public void Draw(SpriteBatch spriteBatch, Rectangle container, float zoom)
        {
            if (!Enabled) { return; }

            UISprite? radiationMainSprite = GUIStyle.Radiation;
            var (offsetX, offsetY) = Map.DrawOffset * zoom;
            var (centerX, centerY) = container.Center.ToVector2();
            var (halfSizeX, halfSizeY) = new Vector2(container.Width / 2f, container.Height / 2f) * zoom;
            float viewBottom = centerY + Map.Height * zoom;
            Vector2 topLeft = new Vector2(centerX + offsetX - halfSizeX, centerY + offsetY - halfSizeY);
            Vector2 size = new Vector2((Amount - increasedAmount) * zoom + halfSizeX, viewBottom - topLeft.Y);
            if (size.X < 0) { return; }

            Vector2 spriteScale = new Vector2(zoom);

            radiationMainSprite?.Sprite.DrawTiled(spriteBatch, topLeft, size, color: Params.RadiationAreaColor, startOffset: Vector2.Zero, textureScale: spriteScale);

            Vector2 topRight = topLeft + Vector2.UnitX * size.X;

            int index = 0;
            if (radiationEdgeAnimSheet != null)
            {
                for (float i = 0; i <= size.Y; i += radiationEdgeAnimSheet.FrameSize.Y / 2f * zoom)
                {
                    bool isEven = ++index % 2 == 0;
                    Vector2 origin = new Vector2(0.5f, 0) * radiationEdgeAnimSheet.FrameSize.X;
                    // every other sprite's animation is reversed to make it seem more chaotic
                    int sprite = (int) MathF.Floor(isEven ? spriteIndex : maxFrames - spriteIndex);
                    radiationEdgeAnimSheet.Draw(spriteBatch, sprite, topRight + new Vector2(0, i), Params.RadiationBorderTint, origin, 0f, spriteScale);
                }
            }

            radiationMultiplier = null;
            if (container.Contains(PlayerInput.MousePosition))
            {
                float rightEdge = topLeft.X + size.X;
                float distanceFromRight = rightEdge - PlayerInput.MousePosition.X;
                if (distanceFromRight >= 0)
                {
                    radiationMultiplier = Math.Min(4, (int)(distanceFromRight / (Params.RadiationEffectMultipliedPerPixelDistance * zoom)) + 1);
                }
            }
        }

        public void DrawFront(SpriteBatch spriteBatch)
        {
            if (radiationMultiplier is int multiplier)
            {
                var tooltip = TextManager.GetWithVariable("RadiationTooltip", "[jovianmultiplier]", multiplier.ToString());
                GUIComponent.DrawToolTip(spriteBatch, tooltip, PlayerInput.MousePosition + new Vector2(18 * GUI.Scale));
            }
        }

        public void MapUpdate(float deltaTime)
        {
            float spriteStep = Params.BorderAnimationSpeed * deltaTime;
            spriteIndex = (spriteIndex + spriteStep) % maxFrames;

            if (increasedAmount > 0)
            {
                increasedAmount -= (lastIncrease / Params.AnimationSpeed) * deltaTime;
            }
        }
    }
}