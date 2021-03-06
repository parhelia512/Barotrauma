Texture2D xTexture;
sampler TextureSampler = sampler_state { Texture = <xTexture>; };

float blurDistance;
float4 color; 

float4 solidColor(float4 position : SV_Position, float4 clr : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float a = tex2D(TextureSampler, texCoord).a;    
    return color * a;
}

float4 solidColorBlur(float4 position : SV_Position, float4 clr : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float sample;
    sample = tex2D(TextureSampler, float2(texCoord.x + blurDistance, texCoord.y + blurDistance)).a;
    sample += tex2D(TextureSampler, float2(texCoord.x - blurDistance, texCoord.y - blurDistance)).a;
    sample += tex2D(TextureSampler, float2(texCoord.x + blurDistance, texCoord.y - blurDistance)).a;
    sample += tex2D(TextureSampler, float2(texCoord.x - blurDistance, texCoord.y + blurDistance)).a;
    sample = sample * 0.25f;
	
    return color * sample;
}

technique SolidColor
{
    pass Pass1
    {
        PixelShader = compile ps_4_0_level_9_1 solidColor();
    }
}
technique SolidColorBlur
{
    pass Pass1
    {
        PixelShader = compile ps_4_0_level_9_1 solidColorBlur();
    }
}