# TODO

- [ ] 添加粘贴时放入实际粘贴内容的特性，粘贴内容上限为400字，超出则保留头尾。
- [x] 开发悬浮球（已完成：GDI+ 绘制、拖拽、边缘吸附、右键菜单、单例）
- [x] 设计数据记录文件夹布局（已完成：data/db, data/sessions, data/exports, data/live, data/log）
- [x] 添加焦点位置探测
- [x] 定期归档/清理数据库（已完成：512KB 阈值自动拆分 session，DbToText 导出）
- [x] 不能编译出weasel，需要确保编译出"modified-weasel"避免冲突（已完成：cb-weasel 改名，新 GUID、注册表路径、DLL 名）
- [x] 确定打包方案（已完成：NSIS 安装包，installer\commitball.nsi + build-installer.ps1）
- [ ] 添加鼠标点击事件的监听，只监听按钮点击事件
- [ ] 悬浮球状态添加当前db大小和分裂进度百分比
- [ ] 鼠标停在悬浮球上时伸展出输入栏，输入栏按下esc退出，或经过2秒后退出；输入内容会直接输出到data下一个单独文件