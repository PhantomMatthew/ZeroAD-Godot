# 0 A.D. 3D模型加载系统详细分析

## 概述

0 A.D.使用了复杂的3D模型加载和渲染系统，支持角色单位、建筑物和地形的渲染。系统采用分层架构，包括模型定义、实例化、材质管理和渲染优化等多个层次。

## 模型加载架构总览

### 核心组件关系图
```
CObjectManager (对象管理器)
├── CObjectEntry (对象实例)
│   ├── CModel (3D模型实例)
│   │   └── CModelDef (模型定义)
│   └── CUnit (游戏单位)
├── CMeshManager (网格管理器)
│   └── CColladaManager (COLLADA转换器)
└── CSkeletonAnimManager (骨骼动画管理器)
```

### 地形渲染系统
```
CTerrain (地形系统)
├── CPatch (地形块，16x16瓦片)
│   └── CMiniPatch (小地形块)
└── TerrainRenderer (地形渲染器)
```

## 角色模型加载详细流程

### 1. 模型文件格式支持

#### COLLADA (.dae) 到引擎格式转换
- **输入格式:** COLLADA .dae 文件 (3D建模软件导出)
- **转换格式:** 
  - `.pmd` (Pyrogenesis Model Data) - 几何数据
  - `.psa` (Pyrogenesis Skeleton Animation) - 骨骼动画数据

#### 转换流程
```cpp
// source/graphics/ColladaManager.cpp
CColladaManager::GetLoadablePath() {
    1. 检查缓存的 .pmd/.psa 是否存在且有效
    2. 如果无效但 .dae 存在，调用转换器
    3. 使用 convert_dae_to_pmd() 和 convert_dae_to_psa()
    4. 缓存转换结果以加速后续加载
}
```

### 2. 角色单位加载步骤

#### 第一步：Actor定义解析
```cpp
// source/graphics/Unit.cpp:50
std::unique_ptr<CUnit> CUnit::Create(const CStrW& actorName, ...)
{
    // 1. 通过ObjectManager加载Actor定义
    // 2. Actor定义包含模型路径、材质、动画集合
    // 3. 支持变体选择(不同装备、颜色等)
}
```

#### 第二步：模型几何数据加载
```cpp
// source/graphics/MeshManager.cpp
CModelDefPtr CMeshManager::GetMesh(const VfsPath& pathname)
{
    // 1. 检查缓存中是否已加载该模型
    // 2. 通过ColladaManager获取.pmd文件路径
    // 3. 加载顶点数据、法线、纹理坐标
    // 4. 构建骨骼层级结构
}
```

#### 第三步：模型实例化
```cpp
// source/graphics/Model.cpp
CModel::CModel(...)
{
    // 1. 从ModelDef创建Model实例
    // 2. 分配骨骼变换矩阵
    // 3. 设置材质和纹理绑定
    // 4. 初始化包围盒用于视锥剔除
}
```

#### 第四步：动画系统绑定
```cpp
// source/graphics/SkeletonAnimManager.cpp
// 1. 加载.psa动画文件
// 2. 验证骨骼数量匹配
// 3. 创建动画播放控制器
// 4. 支持动画混合和过渡
```

### 3. 材质和纹理管理

#### 材质定义 (Material System)
```cpp
// source/graphics/Material.cpp
// 1. XML格式的材质定义文件
// 2. 支持多Pass渲染
// 3. Shader参数绑定
// 4. 纹理单元分配
```

#### 示例材质配置
```xml
<material>
    <shader effect="default" />
    <define name="USE_NORMALMAP" value="1" />
    <texture file="diffuse.dds" name="baseTex" />
    <texture file="normal.dds" name="normTex" />
</material>
```

## 地形加载和渲染系统

### 1. 地形数据结构

#### 层次化地形组织
```cpp
// source/graphics/Terrain.h:41-47
const ssize_t TERRAIN_TILE_SIZE = 4;      // 每瓦片4米
const ssize_t HEIGHT_UNITS_PER_METRE = 92; // 高度精度
const ssize_t PATCH_SIZE = 16;            // 每Patch 16x16瓦片

// 地形组织层次：
// CTerrain -> CPatch (16x16 tiles) -> CMiniPatch -> 单个瓦片
```

#### 地形数据加载
```cpp
// source/graphics/Terrain.cpp
CTerrain::CTerrain()
{
    // 1. 分配高度图数据 (heightmap)
    // 2. 创建Patch网格 
    // 3. 初始化纹理混合权重
    // 4. 设置碰撞检测数据
}
```

### 2. 地形渲染管线

#### Patch-based渲染
```cpp
// source/renderer/TerrainRenderer.cpp
// 1. 视锥剔除 - 只渲染可见Patch
// 2. LOD选择 - 根据距离调整细节级别  
// 3. 纹理混合 - 多层地形纹理合成
// 4. 批处理渲染 - 减少Draw Call
```

#### 地形纹理系统
```cpp
// source/graphics/TerrainTextureManager.cpp
// 1. 支持多层纹理混合 (如草地+泥土)
// 2. 法线贴图用于细节增强
// 3. 纹理图集优化内存使用
// 4. 动态纹理流送
```

## 性能优化技术

### 1. 几何数据优化

#### 顶点缓冲对象(VBO)管理
```cpp
// 1. 静态几何数据存储在GPU内存
// 2. 动态数据(骨骼变换)使用Uniform Buffer
// 3. 顶点数据交错存储提高缓存命中率
// 4. 索引缓冲减少重复顶点
```

#### 批处理渲染
```cpp
// source/renderer/ModelRenderer.cpp
// 1. 按材质分组减少状态切换
// 2. 实例化渲染相同模型的多个实例
// 3. 视锥剔除和遮挡剔除
// 4. LOD (Level of Detail) 系统
```

### 2. 内存管理优化

#### 模型数据共享
```cpp
// source/graphics/MeshManager.cpp
// 1. CModelDef在多个CModel实例间共享
// 2. 弱引用防止循环依赖
// 3. 延迟加载减少内存占用
// 4. LRU缓存管理未使用模型
```

#### 纹理内存管理
```cpp
// source/graphics/TextureManager.cpp
// 1. 纹理压缩 (DXT/BC格式)
// 2. Mipmap自动生成
// 3. 纹理流送系统
// 4. 纹理图集合并小纹理
```

## 动画系统详解

### 1. 骨骼动画架构

#### 骨骼层次结构
```cpp
// source/graphics/ModelDef.h
struct SBone {
    CStr m_Name;           // 骨骼名称
    CMatrix3D m_Transform; // 局部变换矩阵  
    int m_Parent;          // 父骨骼索引
};
```

#### 动画播放控制
```cpp
// source/graphics/UnitAnimation.cpp
// 1. 支持多动画混合 (如移动+攻击)
// 2. 动画状态机管理
// 3. 循环和单次播放模式
// 4. 平滑过渡和插值
```

### 2. 动画数据格式

#### PSA文件结构
```
PSA Header
├── 骨骼信息 (名称、父子关系)
├── 动画序列定义
├── 关键帧数据
└── 时间戳信息
```

## 渲染管线集成

### 1. 场景渲染顺序

#### 渲染流水线
```cpp
// source/renderer/SceneRenderer.cpp 渲染顺序：
// 1. 不透明物体 (前向后渲染)
//    - 地形 Patch
//    - 建筑物和静态物体  
//    - 角色单位
// 2. 半透明物体 (后向前渲染)
//    - 粒子效果
//    - UI叠加层
```

#### 阴影渲染
```cpp
// 1. Shadow Map生成阶段
// 2. 主渲染阶段应用阴影
// 3. 级联阴影贴图用于大范围阴影
// 4. PCF软阴影过滤
```

### 2. 着色器系统

#### 可编程渲染管线
```cpp
// source/graphics/ShaderManager.cpp
// 1. 动态Shader生成基于特性组合
// 2. Uber Shader技术减少变体数量
// 3. Shader热重载支持开发调试
// 4. 平台特定优化 (Desktop/Mobile)
```

## 调试和开发工具

### 1. 模型检视工具
- Atlas编辑器内置模型查看器
- 支持动画预览和调试
- 材质参数实时调整
- 性能分析和统计

### 2. 热重载系统
```cpp
// 文件监控系统支持：
// 1. .dae模型文件修改自动转换
// 2. 纹理和材质实时更新
// 3. Shader代码热重载
// 4. 动画文件动态加载
```

## 文件引用

### 核心模型系统
- **模型加载:** `source/graphics/MeshManager.h/.cpp`
- **模型定义:** `source/graphics/ModelDef.h/.cpp` 
- **模型实例:** `source/graphics/Model.h/.cpp`
- **单位管理:** `source/graphics/Unit.h/.cpp`
- **对象管理:** `source/graphics/ObjectManager.h/.cpp`

### 地形系统
- **地形核心:** `source/graphics/Terrain.h/.cpp`
- **地形块:** `source/graphics/Patch.h/.cpp`
- **地形渲染:** `source/renderer/TerrainRenderer.h/.cpp`
- **小地形块:** `source/graphics/MiniPatch.h/.cpp`

### COLLADA转换
- **转换管理:** `source/graphics/ColladaManager.h/.cpp`
- **DLL接口:** `source/collada/DLL.h/.cpp`

### 动画系统  
- **动画管理:** `source/graphics/SkeletonAnimManager.h/.cpp`
- **动画定义:** `source/graphics/SkeletonAnimDef.h/.cpp`
- **单位动画:** `source/graphics/UnitAnimation.h/.cpp`

### 渲染系统
- **场景渲染:** `source/renderer/SceneRenderer.h/.cpp`
- **模型渲染:** `source/renderer/ModelRenderer.h/.cpp`
- **材质系统:** `source/graphics/Material.h/.cpp`

## 总结

0 A.D.的3D模型加载系统是一个高度优化的多层架构：

1. **灵活的文件格式支持** - 从COLLADA到优化的内部格式
2. **高效的内存管理** - 共享几何数据和智能缓存
3. **先进的动画系统** - 骨骼动画混合和状态管理  
4. **分层地形渲染** - Patch-based系统支持大世界
5. **现代渲染技术** - 实例化、批处理和LOD优化
6. **开发者友好** - 热重载和实时调试工具

这个系统既保证了游戏的视觉质量，又维持了良好的性能表现，为大规模RTS游戏提供了坚实的图形基础。