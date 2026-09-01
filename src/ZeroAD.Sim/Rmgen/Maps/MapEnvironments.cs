using System;
using System.Collections.Generic;
using ZeroAD.Sim.RmgenMath;

namespace ZeroAD.Sim.Rmgen.Maps
{
    /// <summary>各随机地图的环境设置（上游各 *.js 末尾那串 setSkySet/setSun*/setWater*/
    /// setFog*/setPP* 调用）。这些调用是纯状态写入，唯一的副作用是消耗 RNG——
    /// 上游全部位于生成流程尾部（最早的也在文件 85% 之后），因此本表在
    /// <see cref="StandardMap.Generate"/> 末尾统一施加，抽数顺序与上游一致。
    ///
    /// 本文件由 maps/random/*.js 机械提取（见提交说明），改上游后需重新提取。
    /// 依赖图内局部变量的少数调用（多为 setWaterHeight(heightSeaGround)）不在此表，
    /// 由对应地图类覆盖 ApplyEnvironment 自行补。</summary>
    public static class MapEnvironments
    {
        private static readonly Dictionary<string, Action<RmgenEnvironment, RmgenRng, int>> s_table =
            new(StringComparer.Ordinal)
        {
            ["aegean_sea"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cumulus");
                env.SetSunColor(0.866667, 0.776471, 0.486275);
                env.SetWaterColor(0, 0.501961, 1);
                env.SetWaterTint(0.501961, 1, 1);
                env.SetWaterWaviness(4.0);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.49);
                env.SetFogFactor(0.3);
                env.SetFogThickness(0.25);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.62);
                env.SetPPSaturation(0.51);
                env.SetPPBloom(0.12);
            },
            ["african_plains"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetWaterType("clap");
            },
            ["alpine_lakes"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.53);
                env.SetPPBloom(0.12);
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 5, 1.0 / 3)));
                env.SetWaterColor(0.0, 0.047, 0.286);
                env.SetWaterTint(0.471, 0.776, 0.863);
                env.SetWaterMurkiness(0.82);
                env.SetWaterWaviness(3.0);
                env.SetWaterType("clap");
            },
            ["alpine_valley"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 5, 1.0 / 3)));
            },
            ["anatolian_plateau"] = (env, rng, mapSize) =>
            {
                env.SetFogThickness(0.1);
                env.SetFogFactor(0.2);
                env.SetPPEffect("hdr");
                env.SetPPSaturation(0.45);
                env.SetPPContrast(0.62);
                env.SetPPBloom(0.2);
            },
            ["archipelago"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(4.0);
                env.SetWaterType("ocean");
            },
            ["arctic_summer"] = (env, rng, mapSize) =>
            {
                env.SetFogThickness(0.46);
                env.SetFogFactor(0.5);
                env.SetPPEffect("hdr");
                env.SetPPSaturation(0.48);
                env.SetPPContrast(0.53);
                env.SetPPBloom(0.12);
                env.SetSkySet("sunset 1");
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunColor(0.8, 0.7, 0.6);
                env.SetAmbientColor(0.6, 0.5, 0.6);
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 12, 1.0 / 7)));
                env.SetWaterColor(0, 0.047, 0.286);
                env.SetWaterTint(0.462, 0.756, 0.866);
                env.SetWaterMurkiness(0.92);
                env.SetWaterWaviness(1);
                env.SetWaterType("clap");
            },
            ["atlas_mountains"] = (env, rng, mapSize) =>
            {
                env.SetFogFactor(0.2);
                env.SetFogThickness(0.14);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.45);
                env.SetPPSaturation(0.56);
                env.SetPPBloom(0.1);
            },
            ["bahrain"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetSkySet("cloudless");
                env.SetWaterHeight(RmgenLibrary.ScaleByMapSize(20, 18, mapSize));
                env.SetWaterTint(0.37, 0.67, 0.73);
                env.SetWaterColor(0.24, 0.44, 0.56);
                env.SetWaterWaviness(9);
                env.SetWaterMurkiness(0.8);
                env.SetWaterType("lake");
                env.SetAmbientColor(0.521, 0.475, 0.322);
                env.SetSunRotation(SafeMath.PI);
                env.SetSunElevation(SafeMath.PI / (6.25));
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["botswanan_haven"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cirrus");
                env.SetWaterColor(0.553, 0.635, 0.345);
                env.SetWaterTint(0.161, 0.514, 0.635);
                env.SetWaterMurkiness(0.8);
                env.SetWaterWaviness(1.0);
                env.SetWaterType("clap");
                env.SetFogThickness(0.25);
                env.SetFogFactor(0.6);
                env.SetPPEffect("hdr");
                env.SetPPSaturation(0.44);
                env.SetPPBloom(0.3);
            },
            ["cantabrian_highlands"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cirrus");
                env.SetWaterColor(0.447, 0.412, 0.322);
                env.SetWaterTint(0.447, 0.412, 0.322);
                env.SetWaterMurkiness(1.0);
                env.SetWaterWaviness(3.0);
                env.SetWaterType("lake");
                env.SetFogThickness(0.25);
                env.SetFogFactor(0.4);
            },
            ["cappadocian_badlands"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(1.0);
                env.SetWaterType("clap");
                env.SetWaterHeight(20);
            },
            ["coast_range"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(1.0);
                env.SetWaterType("ocean");
            },
            ["continent"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(1.0);
                env.SetWaterType("ocean");
            },
            ["corinthian_isthmus"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(2.5);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.49);
            },
            ["corsica"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetSkySet(rng.PickRandom(new[] { "cumulus", "sunny" }));
                env.SetSunColor(0.8, 0.66, 0.48);
                env.SetSunElevation(0.828932);
                env.SetAmbientColor(0.564706, 0.543726, 0.419608);
                env.SetWaterColor(0.2, 0.294, 0.49);
                env.SetWaterTint(0.208, 0.659, 0.925);
                env.SetWaterMurkiness(0.72);
                env.SetWaterWaviness(2.0);
                env.SetWaterType("ocean");
            },
            ["cycladic_archipelago"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetWaterColor(0.2, 0.294, 0.49);
                env.SetWaterTint(0.208, 0.659, 0.925);
                env.SetWaterMurkiness(0.72);
                env.SetWaterWaviness(3.0);
                env.SetWaterType("ocean");
            },
            ["danubius"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cumulus");
                env.SetSunColor(0.9, 0.8, 0.5);
                env.SetFogFactor(0.05);
                env.SetFogThickness(0.25);
                env.SetWaterColor(0.317, 0.396, 0.294);
                env.SetWaterTint(0.439, 0.403, 0.262);
                env.SetPPContrast(0.62);
                env.SetPPSaturation(0.51);
                env.SetPPBloom(0.12);
                env.SetSkySet("dark");
                env.SetSunColor(0.4, 0.9, 1.2);
                env.SetSunElevation(0.13499);
                env.SetSunRotation(-2.5);
                env.SetAmbientColor(0.25, 0.3, 0.45);
                env.SetFogFactor(0.004);
                env.SetFogThickness(0.25);
                env.SetFogColor(0.35, 0.45, 0.5);
                env.SetWaterColor(0.074, 0.101, 0.090);
                env.SetWaterTint(0.129, 0.160, 0.137);
                env.SetPPEffect("hdr");
                env.SetWaterWaviness(2.0);
                env.SetWaterType("lake");
                env.SetWaterMurkiness(0.97);
                env.SetWaterHeight(21);
            },
            ["dodecanese"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cumulus");
                env.SetSunColor(0.87, 0.78, 0.49);
                env.SetWaterColor(0, 0.501961, 1);
                env.SetWaterTint(0.5, 1, 1);
                env.SetWaterWaviness(4.0);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.49);
                env.SetFogFactor(0.3);
                env.SetFogThickness(0.25);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.62);
                env.SetPPSaturation(0.51);
                env.SetPPBloom(0.12);
            },
            ["english_channel"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cirrus");
                env.SetWaterColor(0.114, 0.192, 0.463);
                env.SetWaterTint(0.255, 0.361, 0.651);
                env.SetWaterWaviness(3.0);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.83);
                env.SetFogThickness(0.35);
                env.SetFogFactor(0.55);
                env.SetPPEffect("hdr");
                env.SetPPSaturation(0.62);
                env.SetPPContrast(0.62);
                env.SetPPBloom(0.37);
            },
            ["extinct_volcano"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("rain");
                env.SetWaterType("lake");
                env.SetWaterWaviness(2);
                env.SetWaterColor(0.1, 0.13, 0.15);
                env.SetWaterTint(0.058, 0.05, 0.035);
                env.SetWaterMurkiness(0.9);
                env.SetPPEffect("hdr");
            },
            ["fields_of_meroe"] = (env, rng, mapSize) =>
            {
                env.SetSunElevation(SafeMath.PI / (8));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunColor(0.746, 0.718, 0.539);
                env.SetWaterColor(0.292, 0.347, 0.691);
                env.SetWaterTint(0.550, 0.543, 0.437);
                env.SetFogColor(0.8, 0.76, 0.61);
                env.SetFogThickness(0.2);
                env.SetFogFactor(0.2);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.65);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.6);
            },
            ["flood"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cloudless", "cumulus", "overcast" }));
                env.SetWaterMurkiness(0.4);
            },
            ["fortress"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetWaterColor(0.157, 0.149, 0.443);
                env.SetWaterTint(0.443, 0.42, 0.824);
                env.SetWaterWaviness(2.0);
                env.SetWaterType("lake");
                env.SetWaterMurkiness(0.83);
                env.SetFogFactor(0.35);
                env.SetFogThickness(0.22);
                env.SetFogColor(0.82, 0.82, 0.73);
                env.SetPPSaturation(0.56);
                env.SetPPContrast(0.56);
                env.SetPPBloom(0.38);
                env.SetPPEffect("hdr");
            },
            ["guadalquivir_river"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cumulus");
                env.SetWaterColor(0.2, 0.312, 0.522);
                env.SetWaterTint(0.1, 0.1, 0.8);
                env.SetWaterWaviness(4.0);
                env.SetWaterType("lake");
                env.SetWaterMurkiness(0.73);
                env.SetFogFactor(0.3);
                env.SetFogThickness(0.25);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.62);
                env.SetPPSaturation(0.51);
                env.SetPPBloom(0.12);
            },
            ["harbor"] = (env, rng, mapSize) =>
            {
                env.SetFogFactor(0.04);
            },
            ["hellas"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunColor(0.988166, 0.929297, 0.693819);
                env.SetSunElevation(0.579592);
                env.SetSunRotation(-0.566729);
                env.SetAmbientColor(0.372549, 0.376471, 0.459608);
                env.SetWaterColor(0.024, 0.212, 0.024);
                env.SetWaterTint(0.133, 0.725, 0.855);
                env.SetWaterMurkiness(0.8);
                env.SetWaterWaviness(3);
                env.SetFogFactor(0);
                env.SetPPEffect("hdr");
                env.SetPPSaturation(0.45);
                env.SetPPContrast(0.62);
                env.SetPPBloom(0.12);
            },
            ["hyrcanian_shores"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(2.0);
                env.SetWaterType("ocean");
            },
            ["india"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.87451, 0.847059, 0.647059);
                env.SetWaterColor(0.741176, 0.592157, 0.27451);
                env.SetWaterTint(0.741176, 0.592157, 0.27451);
                env.SetWaterWaviness(2.0);
                env.SetWaterType("clap");
                env.SetWaterMurkiness(0.835938);
                env.SetAmbientColor(0.57, 0.58, 0.55);
                env.SetFogFactor(0.25);
                env.SetFogThickness(0.15);
                env.SetFogColor(0.847059, 0.737255, 0.482353);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.57031);
                env.SetPPBloom(0.34);
            },
            ["island_stronghold"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cloudless", "cumulus", "overcast" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation((rng.RandFloat(1.0 / 5, 1.0 / 3)) * SafeMath.PI);
                env.SetWaterWaviness(2);
            },
            ["islands"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation((rng.RandFloat(1.0 / 5, 1.0 / 3)) * SafeMath.PI);
                env.SetWaterWaviness(2);
            },
            ["jebel_barkal"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetWindAngle(-0.43);
                env.SetWaterTint(0.161, 0.286, 0.353);
                env.SetWaterColor(0.129, 0.176, 0.259);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.87);
                env.SetWaterType("lake");
                env.SetAmbientColor(0.58, 0.443, 0.353);
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetSunElevation(SafeMath.PI / (7));
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["kerala"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.6, 0.6, 0.6);
                env.SetSunElevation(SafeMath.PI / (3));
                env.SetWaterColor(0.524, 0.734, 0.839);
                env.SetWaterTint(0.369, 0.765, 0.745);
                env.SetWaterWaviness(1.0);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.35);
                env.SetFogFactor(0.4);
                env.SetFogThickness(0.2);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.7);
                env.SetPPSaturation(0.65);
                env.SetPPBloom(0.6);
                env.SetSkySet("cirrus");
            },
            ["lake"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(4.0);
                env.SetWaterType("lake");
            },
            ["land_grab"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation((rng.RandFloat(1.0 / 5, 1.0 / 3)) * SafeMath.PI);
                env.SetWaterWaviness(2);
            },
            ["latium"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetWaterColor(0.024, 0.262, 0.224);
                env.SetWaterTint(0.133, 0.325, 0.255);
                env.SetWaterWaviness(2.5);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.8);
            },
            ["lorraine_plain"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cirrus");
                env.SetWaterColor(0.1, 0.212, 0.422);
                env.SetWaterTint(0.3, 0.1, 0.949);
                env.SetWaterWaviness(3.0);
                env.SetWaterType("lake");
                env.SetWaterMurkiness(0.80);
            },
            ["lower_nubia"] = (env, rng, mapSize) =>
            {
                env.SetWindAngle(-0.43);
                env.SetWaterTint(0.161, 0.286, 0.353);
                env.SetWaterColor(0.129, 0.176, 0.259);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.87);
                env.SetWaterType("lake");
                env.SetAmbientColor(0.58, 0.443, 0.353);
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetSunRotation(SafeMath.PI * (1.1));
                env.SetSunElevation(SafeMath.PI / (7));
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["marmara"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.753, 0.586, 0.584);
                env.SetSkySet("sunset");
                env.SetWaterHeight(RmgenLibrary.ScaleByMapSize(20, 18, mapSize));
                env.SetWaterTint(0.25, 0.67, 0.65);
                env.SetWaterColor(0.18, 0.36, 0.39);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.99);
                env.SetWaterType("lake");
                env.SetAmbientColor(0.521, 0.475, 0.322);
                env.SetSunRotation(SafeMath.PI * (0.85));
                env.SetSunElevation(SafeMath.PI / (14));
                env.SetFogFactor(0.15);
                env.SetFogThickness(0);
                env.SetFogColor(0.64, 0.5, 0.35);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["mediterranean"] = (env, rng, mapSize) =>
            {
                env.SetWindAngle(-0.589049);
                env.SetWaterTint(0.556863, 0.615686, 0.643137);
                env.SetWaterColor(0.494118, 0.639216, 0.713726);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.87);
                env.SetWaterType("ocean");
                env.SetAmbientColor(0.72, 0.72, 0.82);
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetSunRotation(SafeMath.PI * (0.95));
                env.SetSunElevation(SafeMath.PI / (6));
                env.SetSkySet("cumulus");
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["migration"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation((rng.RandFloat(1.0 / 5, 1.0 / 3)) * SafeMath.PI);
                env.SetWaterWaviness(2);
            },
            ["ngorongoro"] = (env, rng, mapSize) =>
            {
                env.SetAmbientColor(0.521, 0.475, 0.322);
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetSunRotation(SafeMath.PI);
                env.SetSunElevation(1.0 / 2);
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["northern_lights"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.6, 0.6, 0.6);
                env.SetSunElevation(SafeMath.PI / (6));
                env.SetWaterColor(0.02, 0.17, 0.52);
                env.SetWaterTint(0.494, 0.682, 0.808);
                env.SetWaterMurkiness(0.82);
                env.SetWaterWaviness(0.5);
                env.SetWaterType("ocean");
                env.SetFogFactor(0.95);
                env.SetFogThickness(0.09);
                env.SetPPSaturation(0.28);
                env.SetPPEffect("hdr");
                env.SetSkySet("fog");
            },
            ["oasis"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunColor(0.914, 0.827, 0.639);
                env.SetSunRotation(SafeMath.PI / (3));
                env.SetSunElevation(0.5);
                env.SetWaterColor(0, 0.227, 0.843);
                env.SetWaterTint(0, 0.545, 0.859);
                env.SetWaterWaviness(1.0);
                env.SetWaterType("clap");
                env.SetWaterMurkiness(0.5);
                env.SetAmbientColor(0.501961, 0.501961, 0.501961);
            },
            ["phoenician_levant"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunColor(0.917, 0.828, 0.734);
                env.SetWaterColor(0.263, 0.314, 0.631);
                env.SetWaterTint(0.133, 0.725, 0.855);
                env.SetWaterWaviness(2.0);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.8);
                env.SetAmbientColor(0.447059, 0.509804, 0.54902);
                env.SetSunElevation(0.671884);
                env.SetSunRotation(-0.582913);
                env.SetFogFactor(0.2);
                env.SetFogThickness(0.15);
                env.SetFogColor(0.8, 0.7, 0.6);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.53);
                env.SetPPSaturation(0.47);
                env.SetPPBloom(0.52);
            },
            ["polar_sea"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunset 1");
                env.SetSunColor(0.8, 0.7, 0.6);
                env.SetAmbientColor(0.7, 0.6, 0.7);
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 24, 1.0 / 7)));
                env.SetSkySet(rng.PickRandom(new[] { "cumulus", "rain", "mountainous", "overcast", "rain", "stratus" }));
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 9, 1.0 / 7)));
                env.SetSunRotation(rng.RandomAngle());
                env.SetWaterColor(0.3, 0.3, 0.4);
                env.SetWaterTint(0.75, 0.75, 0.75);
                env.SetWaterMurkiness(0.92);
                env.SetWaterWaviness(0.5);
                env.SetWaterType("clap");
                env.SetFogThickness(0.76);
                env.SetFogFactor(0.7);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.6);
                env.SetPPSaturation(0.45);
                env.SetPPBloom(0.4);
            },
            ["pompeii"] = (env, rng, mapSize) =>
            {
                env.SetWaterTint(0.5, 0.5, 0.5);
                env.SetWaterColor(0.3, 0.3, 0.3);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.87);
                env.SetWaterType("lake");
                env.SetAmbientColor(0.3, 0.3, 0.3);
                env.SetSunColor(0.8, 0.8, 0.8);
                env.SetSunRotation(SafeMath.PI);
                env.SetSunElevation(1.0 / 2);
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetSkySet("stormy");
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["pyrenean_sierra"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 5, 1.0 / 3)));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSkySet("cumulus");
                env.SetSunColor(0.73, 0.73, 0.65);
                env.SetAmbientColor(0.45, 0.45, 0.50);
                env.SetWaterColor(0.263, 0.353, 0.616);
                env.SetWaterTint(0.104, 0.172, 0.563);
                env.SetWaterWaviness(5.0);
                env.SetWaterType("ocean");
                env.SetWaterMurkiness(0.83);
            },
            ["ratumacos"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetWaterTint(0.224, 0.271, 0.270);
                env.SetWaterColor(0.224, 0.271, 0.270);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.87);
                env.SetWaterType("clap");
                env.SetAmbientColor(0.521, 0.475, 0.322);
                env.SetSunRotation(-SafeMath.PI);
                env.SetSunElevation(SafeMath.PI / (6.25));
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["red_sea"] = (env, rng, mapSize) =>
            {
                env.SetWindAngle(-0.43);
                env.SetWaterTint(0.161, 0.286, 0.353);
                env.SetWaterColor(0.129, 0.176, 0.259);
                env.SetWaterWaviness(8);
                env.SetWaterMurkiness(0.87);
                env.SetWaterType("lake");
                env.SetAmbientColor(0.58, 0.443, 0.353);
                env.SetSunColor(0.733, 0.746, 0.574);
                env.SetSunRotation(SafeMath.PI * (1.1));
                env.SetSunElevation(SafeMath.PI / (7));
                env.SetFogFactor(0);
                env.SetFogThickness(0);
                env.SetFogColor(0.69, 0.616, 0.541);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.67);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.23);
            },
            ["rhine_marshlands"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("cirrus");
                env.SetWaterColor(0.753, 0.635, 0.345);
                env.SetWaterTint(0.161, 0.514, 0.635);
                env.SetWaterMurkiness(0.8);
                env.SetWaterWaviness(1.0);
                env.SetWaterType("clap");
                env.SetFogThickness(0.25);
                env.SetFogFactor(0.6);
                env.SetPPEffect("hdr");
                env.SetPPSaturation(0.44);
                env.SetPPBloom(0.3);
            },
            ["river_archipelago"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.6, 0.6, 0.6);
                env.SetSunElevation(SafeMath.PI / (3));
                env.SetWaterColor(0.424, 0.534, 0.639);
                env.SetWaterTint(0.369, 0.765, 0.745);
                env.SetWaterWaviness(1.0);
                env.SetWaterType("default");
                env.SetWaterMurkiness(0.35);
                env.SetFogFactor(0.03);
                env.SetFogThickness(0.2);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.7);
                env.SetPPSaturation(0.65);
                env.SetPPBloom(0.6);
                env.SetSkySet("stratus");
            },
            ["rivers"] = (env, rng, mapSize) =>
            {
                env.SetWaterWaviness(3.0);
                env.SetWaterType("lake");
            },
            ["saharan_oases"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunColor(0.746, 0.718, 0.539);
                env.SetWaterColor(0, 0.227, 0.843);
                env.SetWaterTint(0, 0.545, 0.859);
                env.SetWaterWaviness(1.0);
                env.SetWaterType("clap");
                env.SetWaterMurkiness(0.5);
            },
            ["sahel"] = (env, rng, mapSize) =>
            {
                env.SetSunColor(0.87451, 0.847059, 0.647059);
                env.SetWaterColor(0.741176, 0.592157, 0.27451);
                env.SetWaterTint(0.741176, 0.592157, 0.27451);
                env.SetWaterWaviness(2.0);
                env.SetWaterType("clap");
                env.SetWaterMurkiness(0.835938);
                env.SetAmbientColor(0.447059, 0.509804, 0.54902);
                env.SetFogFactor(0.25);
                env.SetFogThickness(0.15);
                env.SetFogColor(0.847059, 0.737255, 0.482353);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.57031);
                env.SetPPBloom(0.34);
            },
            ["sahel_watering_holes"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 5, 1.0 / 4)));
                env.SetWaterColor(0.478, 0.42, 0.384);
                env.SetWaterTint(0.58, 0.22, 0.067);
                env.SetWaterMurkiness(0.87);
                env.SetWaterWaviness(0.5);
                env.SetWaterType("clap");
            },
            ["schwarzwald"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetSkySet("fog");
                env.SetFogFactor(0.35);
                env.SetFogThickness(0.19);
                env.SetWaterColor(0.501961, 0.501961, 0.501961);
                env.SetWaterTint(0.25098, 0.501961, 0.501961);
                env.SetWaterWaviness(0.5);
                env.SetWaterType("clap");
                env.SetWaterMurkiness(0.75);
                env.SetPPSaturation(0.37);
                env.SetPPContrast(0.4);
                env.SetPPBrightness(0.4);
                env.SetPPEffect("hdr");
                env.SetPPBloom(0.4);
            },
            ["scythian_rivulet"] = (env, rng, mapSize) =>   // 另有依赖局部变量的调用，见地图类
            {
                env.SetSkySet(rng.PickRandom(new[] { "fog", "stormy", "sunset" }));
                env.SetSunElevation(0.27);
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunColor(0.746, 0.718, 0.539);
                env.SetWaterColor(0.292, 0.347, 0.691);
                env.SetWaterTint(0.550, 0.543, 0.437);
                env.SetWaterMurkiness(0.83);
                env.SetWaterType("clap");
                env.SetFogColor(0.8, 0.76, 0.61);
                env.SetFogThickness(2);
                env.SetFogFactor(1.2);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.65);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.6);
            },
            ["snowflake_searocks"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 5, 1.0 / 3)));
            },
            ["syria"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunElevation(SafeMath.PI / (8));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunColor(0.746, 0.718, 0.539);
                env.SetWaterColor(0.292, 0.347, 0.691);
                env.SetWaterTint(0.550, 0.543, 0.437);
                env.SetWaterMurkiness(0.83);
                env.SetFogColor(0.8, 0.76, 0.61);
                env.SetFogThickness(0.2);
                env.SetFogFactor(0.4);
                env.SetPPEffect("hdr");
                env.SetPPContrast(0.65);
                env.SetPPSaturation(0.42);
                env.SetPPBloom(0.6);
            },
            ["the_nile"] = (env, rng, mapSize) =>
            {
                env.SetSkySet("sunny");
                env.SetSunColor(0.711, 0.746, 0.574);
                env.SetWaterColor(0.541, 0.506, 0.416);
                env.SetWaterTint(0.694, 0.592, 0.522);
                env.SetWaterMurkiness(1);
                env.SetWaterWaviness(3.0);
                env.SetWaterType("lake");
            },
            ["unknown"] = (env, rng, mapSize) =>
            {
                env.SetSkySet(rng.PickRandom(new[] { "cirrus", "cumulus", "sunny", "sunny 1", "mountainous", "stratus" }));
                env.SetSunRotation(rng.RandomAngle());
                env.SetSunElevation(SafeMath.PI * (rng.RandFloat(1.0 / 5, 1.0 / 3)));
            },
        };

        /// <summary>施加该图的环境设置（无表项则保持默认环境）。</summary>
        public static void Apply(string mapName, RmgenEnvironment env, RmgenRng rng, int mapSize)
        {
            if (s_table.TryGetValue(mapName, out var apply))
                apply(env, rng, mapSize);
        }

        public static bool Has(string mapName) => s_table.ContainsKey(mapName);
    }
}
