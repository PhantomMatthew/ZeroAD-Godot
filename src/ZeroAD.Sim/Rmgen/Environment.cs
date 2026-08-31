using System;

namespace ZeroAD.Sim.Rmgen
{
    /// <summary>RGB 颜色（原版 environment.js 的 {r,g,b,a}，a 恒 0）。</summary>
    public readonly struct RmgenColor
    {
        public readonly double R, G, B;
        public RmgenColor(double r, double g, double b) { R = r; G = g; B = b; }
    }

    /// <summary>水体设置（原版 g_Environment.Water.WaterBody）。</summary>
    public sealed class RmgenWaterBody
    {
        /// <summary>art/textures/animated/water 下的子目录名。</summary>
        public string Type = "ocean";
        public RmgenColor Color = new(0.3, 0.35, 0.7);
        public RmgenColor Tint = new(0.28, 0.3, 0.59);

        /// <summary>水面高度（上游默认 undefined，由 ExportMap 补；null = 未设定，
        /// 消费方回退 SEA_LEVEL）。</summary>
        public double? Height;

        public double Waviness = 8;
        public double Murkiness = 0.45;
        public double WindAngle;

        public RmgenWaterBody Clone() => (RmgenWaterBody)MemberwiseClone();
    }

    /// <summary>雾设置（原版 g_Environment.Fog）。</summary>
    public sealed class RmgenFog
    {
        public double FogFactor;
        public double FogThickness = 0.5;
        public RmgenColor FogColor = new(0.8, 0.8, 0.8);

        public RmgenFog Clone() => (RmgenFog)MemberwiseClone();
    }

    /// <summary>后处理设置（原版 g_Environment.Postproc）。</summary>
    public sealed class RmgenPostproc
    {
        public double Brightness;
        public double Contrast = 1.0;
        public double Saturation = 1.0;
        public double Bloom = 0.2;

        /// <summary>"default" / "hdr" / "DOF"。</summary>
        public string PostprocEffect = "default";

        public RmgenPostproc Clone() => (RmgenPostproc)MemberwiseClone();
    }

    /// <summary>地图环境（逐字移植 rmgen/environment.js 的 g_Environment + set* 系列）。
    /// 天空/太阳/环境光/水体/雾/后处理——决定地图的整体氛围。
    /// 默认值即上游字面量默认值；set* 方法保留上游的数值变换
    /// （setFogFactor 除以 100、setPP* 的偏移与缩放）。</summary>
    public sealed class RmgenEnvironment
    {
        /// <summary>art/textures/skies 下的子目录名。</summary>
        public string SkySet = "default";

        public RmgenColor SunColor = new(1.03162, 0.99521, 0.865752);

        /// <summary>0 到 2π。</summary>
        public double SunElevation = 0.7;

        /// <summary>0 到 2π。</summary>
        public double SunRotation = -0.909;

        public RmgenColor AmbientColor = new(0.364706, 0.376471, 0.419608);

        public RmgenWaterBody Water = new();
        public RmgenFog Fog = new();
        public RmgenPostproc Postproc = new();

        // ── 天空 / 太阳 / 地表光照 ──

        public void SetSkySet(string set) => SkySet = set;
        public void SetSunColor(double r, double g, double b) => SunColor = new RmgenColor(r, g, b);
        public void SetSunElevation(double e) => SunElevation = e;
        public void SetSunRotation(double r) => SunRotation = r;
        public void SetAmbientColor(double r, double g, double b)
            => AmbientColor = new RmgenColor(r, g, b);

        // ── 水 ──

        public void SetWaterColor(double r, double g, double b)
            => Water.Color = new RmgenColor(r, g, b);
        public void SetWaterTint(double r, double g, double b)
            => Water.Tint = new RmgenColor(r, g, b);
        public void SetWaterHeight(double h) => Water.Height = h;
        public void SetWaterWaviness(double w) => Water.Waviness = w;
        public void SetWaterMurkiness(double m) => Water.Murkiness = m;
        public void SetWaterType(string t) => Water.Type = t;
        public void SetWindAngle(double a) => Water.WindAngle = a;

        // ── 雾（上游 setFogFactor 的实参是 0–100 的百分数）──

        public void SetFogFactor(double s) => Fog.FogFactor = s / 100.0;
        public void SetFogThickness(double thickness) => Fog.FogThickness = thickness;
        public void SetFogColor(double r, double g, double b)
            => Fog.FogColor = new RmgenColor(r, g, b);

        // ── 后处理（实参 0–1，上游各自做一次线性变换）──

        public void SetPPBrightness(double s) => Postproc.Brightness = s - 0.5;
        public void SetPPContrast(double s) => Postproc.Contrast = s + 0.5;
        public void SetPPSaturation(double s) => Postproc.Saturation = s * 2;
        public void SetPPBloom(double s) => Postproc.Bloom = (1 - s) * 0.2;
        public void SetPPEffect(string s) => Postproc.PostprocEffect = s;

        public RmgenEnvironment Clone()
        {
            var c = (RmgenEnvironment)MemberwiseClone();
            c.Water = Water.Clone();
            c.Fog = Fog.Clone();
            c.Postproc = Postproc.Clone();
            return c;
        }
    }
}
