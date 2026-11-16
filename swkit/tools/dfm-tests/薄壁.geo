// 可制造性测试件：薄壁。底座 30×20×3，上面一片 0.5 mm 厚、15 mm 高的薄壁。
SetFactory("OpenCASCADE");
Box(1) = {0, 0, 0, 30, 20, 3};
Box(2) = {0, 9.75, 3, 30, 0.5, 15};
BooleanUnion{ Volume{1}; Delete; }{ Volume{2}; Delete; }
