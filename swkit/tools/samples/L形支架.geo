// 示例：L 形支架（毫米）。底板 80×40×6，两个 M6 安装孔；立板高 60，内角 R5 圆角。
SetFactory("OpenCASCADE");
r = 5;
Point(1) = {0, 0, 0}; Point(2) = {80, 0, 0}; Point(3) = {80, 6, 0}; Point(4) = {6 + r, 6, 0};
Point(5) = {6 + r, 6 + r, 0}; Point(6) = {6, 6 + r, 0}; Point(7) = {6, 60, 0}; Point(8) = {0, 60, 0};
Line(1) = {1, 2}; Line(2) = {2, 3}; Line(3) = {3, 4}; Circle(4) = {4, 5, 6}; Line(5) = {6, 7}; Line(6) = {7, 8}; Line(7) = {8, 1};
Curve Loop(1) = {1, 2, 3, 4, 5, 6, 7};
Plane Surface(1) = {1};
Extrude {0, 0, 40} { Surface{1}; }
Cylinder(10) = {45, -1, 20, 0, 8, 0, 3.3};
Cylinder(11) = {68, -1, 20, 0, 8, 0, 3.3};
BooleanDifference{ Volume{1}; Delete; }{ Volume{10, 11}; Delete; }
