# 项目：坦克大战 AI（CE6127 课程作业）

Unity 坦克对战作业：3 台 AI 坦克 vs 1 台真人玩家坦克。核心是让学生练习 **FSM / Behavior Tree / Utility AI** 等 AI 决策方法。

## 项目目标（重要 —— 这不是娱乐项目）

- 这是一个**学习型作业**，目的是练习 AI 决策方法，**玩家体验不重要**。
- 核心目标优先级：
  1. 用尽所有被允许的策略，**优先击败玩家的坦克**。
  2. 在击败玩家的基础上，**尽量保证 AI 坦克的生存**。
- 不同队伍之间会进行比赛（tournament），胜负看计分。

## 硬约束（作业规则）

### 可以修改
- AI 决策逻辑、新增 AI 脚本与行为、FSM 状态与转换、行为树逻辑、Utility 评分、Platoon 角色与协同、AI 相关注释。

### 不可以修改
- 关卡/环境、资源替换、视觉/动画/UI、现有 HP/速度/伤害数值、Rigidbody/Collider/NavMesh/输入/粒子设置、包版本。

#### Prefab 里序列化的固定数值（一律不可修改）

以下数值是项目在 prefab 里写死的，改动即破坏规则。⚠️ 这些是 prefab 的序列化值，会**覆盖**代码里的默认值。

**AI 坦克 `Assets/Prefabs/AI/Tank-VarSM.prefab`**

| 组件 | 字段 | prefab 值 | 说明 |
|---|---|---|---|
| TankSM | `StopAtTargetDist` | (18, 22) | 交战距离基准 |
| TankSM | `FireInterval` | (0.7, 2.5) | 射击冷却，最快 0.7s/发 |
| TankSM | `LaunchForceMinMax` | (7.5, 30) | 发射力度（**prefab 是 7.5，不是代码默认 6.5**） |
| TankSM | `PatrolWaitTime` | (1.5, 3.5) | 巡逻等待（现已不用） |
| TankSM | `PatrolMaxDist` | (15, 30) | 巡逻范围（现已不用） |
| TankSM | `PatrolNavMeshUpdate` | 0.2 | 巡逻路径更新间隔 |
| TankSM | `StartToTargetDist` | (28, 35) | 目标距离（现已不用） |
| TankSM | `TargetNavMeshUpdate` | 0.2 | 追击/交战路径更新间隔 |
| TankSM | `OrientSlerpScalar` | 0.2 | 转向插值标量 |
| NavMeshAgent | `Speed` | 12 | 寻路速度 |
| NavMeshAgent | `AngularSpeed` | 180 | 寻路转向角速度 |
| NavMeshAgent | `Acceleration` | 8 | 加速度 |

**血量 / 伤害**

| 位置 | 字段 | 值 |
|---|---|---|
| `Assets/Prefabs/AI/Tank.prefab`（TankHealth） | `StartingHealth` | 100 |
| `Assets/Prefabs/AI/Shell-VarSM.prefab`（ShellExplosion） | `MaxDamage` | 10 |
| 同上 | `ExplosionForce` | 750 |
| 同上 | `MaxLifeTime` | 1.75 |
| 同上 | `ExplosionRadius` | 3.33 |

**全局 `Assets/Prefabs/AI/GameManager.prefab`**

| 字段 | 值 |
|---|---|
| `Speed` | 12 |
| `AngularSpeed` | 180 |

> 我方新增的调参字段（`AcquireRange`、`AimSlerpScalar`、`MinFireRange`、`AimToleranceDeg`、`OrbitAngularSpeed`、`FlankRadiusMultiplier`）**不在 prefab 里**，走代码默认值，属于可调的 AI 策略参数。

### 可用策略
- FSM、Behavior Tree、Utility AI，三者可组合。

### 禁止
- 机器学习 / 强化学习 / 神经网络 / LLM 等。

## 比赛结构（Tournament Structure）

- **Stage 1 · Round Robin**：同班每队互相对战；每轮分别记录 AI 得分与 Human 得分；前两名直接进 Final。
- **Stage 2 · Final**：第 1 名 vs 第 2 名。胜者 Champion → A+，负者 1st Runner-up → A，其余队伍 B+ 或 B。

## 计分机制（Match Scoring Mechanics）

每一轮**同时**记录 AI 得分和 Human 得分，互不影响：

- **AI Points**：若玩家坦克在全部 AI 坦克被摧毁前先被摧毁，AI 排得 **3 分**。
- **Human Points**：玩家每摧毁一台 AI 坦克，玩家得 **1 分**。

| 回合结果 | AI 被摧毁数 | AI 得分 | Human 得分 |
|---|---|---|---|
| 玩家被摧毁，AI 全存活 | 0 | 3 | 0 |
| 玩家被摧毁，剩 2 台 AI | 1 | 3 | 1 |
| 玩家被摧毁，剩 1 台 AI | 2 | 3 | 2 |
| 3 台 AI 全被摧毁 | 3 | 0 | 3 |

> 时间耗尽且玩家存活：该回合 AI 不得分（0 或 1 或 2 台 AI 被摧毁）。

计分已在 `Assets/Scripts/AI/Managers/GameManager.cs` 实现（`PointsRoundWin = 3` 对应 AI 胜利的 3 分；每台 AI 摧毁 = 玩家 1 分，按 `NumTanks - NumTanksLeft` 折算）。

## 代码架构速览

- **主代码在 `Assets/Scripts/AI/`**（namespace `CE6127.Tanks.AI`）—— 当前 `MainAI.unity` 场景真正运行的就是这一套。
- `Assets/Scripts/Main/`、`Assets/UnityLearn-CompletedAssets/` 是参考/历史代码，改动认准 `AI/`。
- **Manager 层**（规则 + 生成，不碰坦克帧级行为）：
  - `GameManager`（单例）→ 回合状态机、计分、胜负判定。
  - `PlatoonManager` 基类 + `PlatoonManagerSM`（AI 排，`Size=3`）/ `PlatoonManagerPlayer`（玩家排）→ 生成/复位。
  - `TankManager` → 单台坦克的 spawn/reset/enable-disable。
- **两种坦克驱动方式不同**：
  - 玩家：`TankMovement` + `TankShooting`（新输入系统，蓄力开火）。
  - AI：`TankSM`（FSM）+ `NavMeshAgent`（寻路）。
- **状态机**：`StateMachine`（基类）→ `BaseState` → `Idle` / `Patrolling` / `Chase` / `Attack`（`Assets/Scripts/AI/Tank/StateMachine/States/`）。

## AI 信息获取现状（全知视角 / god's eye view）

- AI 通过全局单例 `GameManager.Instance.PlayerPlatoon.Tanks` 直接遍历所有玩家坦克，拿 `tank.Instance.transform.position` —— **没有感知范围、视野或遮挡限制**。
- 逐项盘点：
  - **位置**：随时、无限距离可读。
  - **速度**：`TankSM.TickTargetTracking()` 前后两帧差分算出，同样无限制。
  - **存活**：`tank.Instance.activeSelf`（死亡时 `SetActive(false)`）。
  - **HP**：目前**未读玩家 HP**（只读自己的，用于残血拉开距离），但技术上随时可读。
- 两个易混淆概念（都**不是**感知范围）：
  - `AcquireRange`（索敌距离）只是状态机决策阈值（决定 Patrol→Chase），不是"能不能看见"。
  - `HasLineOfSight()` 只是开火前的射线检测（决定"能不能打中"），不是"能不能发现"。
- 若要实现"有限感知"（玩家进范围/视野内 AI 才有权获取位置），需在 `TankSM.SelectTarget()` 加门控。

## 协作约定

- 做非平凡改动前，先与用户沟通方案并获其同意，再动手。
- 用户问简单问题时，直接简洁回答，不要过度思考、不要长篇展开。
