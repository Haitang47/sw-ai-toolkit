// 示例：悬臂块（毫米），用于拓扑优化。160×60×40，左端面固定，右端面中间一条 8 mm 宽的区域受向下的力。
SetFactory("OpenCASCADE");
Box(1) = {0, 0, 0, 160, 60, 40};
// 在右端面上压印出受力区域（不切开实体，只把面分成几块）
Rectangle(10) = {0, 26, 0, 40, 8};
Rotate {{0, 1, 0}, {0, 0, 0}, -Pi/2} { Surface{10}; }
Translate {160, 0, 0} { Surface{10}; }
BooleanFragments{ Volume{1}; Delete; }{ Surface{10}; Delete; }
