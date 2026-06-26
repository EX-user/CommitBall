# Eye of Commit 皮肤配置

`skin.json` 用来微调素材图层的位置、大小和动画响应。保存文件后，BallShell 会按文件更新时间重新读取；如果当前画面没有立即变化，切换一次皮肤或重启 BallShell 即可。

所有 `Scale` 都是相对悬浮球半径的倍数。所有 `OffsetX`、`OffsetY` 都是相对悬浮球半径的偏移，`X` 为正表示向右，`Y` 为正表示向下。

## 图层参数

- `TentacleScale`：触手层大小。
- `TentacleOffsetX` / `TentacleOffsetY`：触手层位置。
- `BodyScaleX` / `BodyScaleY`：眼球背景圆盘大小。
- `BodyOffsetX` / `BodyOffsetY`：眼球背景圆盘位置。
- `ClipRadiusX` / `ClipRadiusY`：虹膜和血丝的裁剪范围，按眼球背景圆盘中心裁剪。
- `IrisScaleX` / `IrisScaleY`：虹膜和血丝层大小。
- `IrisOffsetX` / `IrisOffsetY`：虹膜和血丝层相对视线中心的位置修正。
- `PupilScaleX` / `PupilScaleY`：瞳孔层大小。
- `PupilOffsetX` / `PupilOffsetY`：瞳孔层相对虹膜中心的位置修正。

## 动画参数

- `GazeOffsetX` / `GazeOffsetY`：鼠标追踪和随机游走时，虹膜与瞳孔可移动的幅度。
- `IrisProjectionStrength`：虹膜和血丝的球面转动透视强度，`0` 表示不受透视影响。
- `PupilProjectionStrength`：瞳孔的球面转动透视强度，`0` 表示不受透视影响。
- `PupilMorphScaleX`：眨眼或点击半眯时，瞳孔横向放大的幅度。
- `PupilMorphScaleY`：眨眼或点击半眯时，瞳孔纵向压缩的幅度。
- `PupilMorphMinY`：瞳孔纵向压缩的最小比例，避免被压到不可见。
