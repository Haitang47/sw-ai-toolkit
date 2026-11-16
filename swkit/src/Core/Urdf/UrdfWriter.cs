using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SwKit.Core.Urdf
{
    /// <summary>写出同时支持 ROS 1 和 ROS 2 的功能包：urdf、meshes、launch、rviz、package.xml、CMakeLists.txt、README。</summary>
    public static class UrdfWriter
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string SanitizeName(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in (s ?? "").ToLowerInvariant())
                sb.Append(ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' ? ch : '_');
            var r = sb.ToString().Trim('_');
            while (r.Contains("__")) r = r.Replace("__", "_");
            if (r.Length == 0 || !char.IsLetter(r[0])) r = "robot" + (r.Length > 0 ? "_" + r : "");
            return r;
        }

        public static string PackageName(RobotModel m) => m.RobotName + "_description";

        static string N(double v) => Math.Abs(v) < 1e-15 ? "0" : v.ToString("G10", CultureInfo.InvariantCulture);
        static string V(Vec3 v) => $"{N(Clean(v.X))} {N(Clean(v.Y))} {N(Clean(v.Z))}";
        static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : Math.Round(v, 9);

        /// <summary>写出整个功能包，返回 ROS 用的 urdf 路径。</summary>
        public static string Write(RobotModel model, MeshResult meshes, string pkgDir)
        {
            var pkg = PackageName(model);
            foreach (var d in new[] { "urdf", "meshes", "launch", "rviz" }) Directory.CreateDirectory(Path.Combine(pkgDir, d));

            var rosUrdf = Path.Combine(pkgDir, "urdf", model.RobotName + ".urdf");
            File.WriteAllText(rosUrdf, Urdf(model, meshes, (l, col) => $"package://{pkg}/meshes/{(col ? "collision/" : "")}{l.Name}.stl"), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "urdf", model.RobotName + "_standalone.urdf"), Urdf(model, meshes, (l, col) => $"../meshes/{(col ? "collision/" : "")}{l.Name}.stl"), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "package.xml"), PackageXml(pkg), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "CMakeLists.txt"), CMake(pkg), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "launch", "display.launch.py"), Launch(pkg, model.RobotName), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "launch", "display.launch"), LaunchRos1(pkg, model.RobotName), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "rviz", "display.rviz"), Rviz(), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "rviz", "display_ros1.rviz"), RvizRos1(), Utf8);
            File.WriteAllText(Path.Combine(pkgDir, "README.md"), Readme(model, pkg), Utf8);
            return rosUrdf;
        }

        public static string Urdf(RobotModel model, MeshResult meshes, Func<Link, bool, string> meshPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine($"<!-- 由 SW 工具箱从 SolidWorks 装配体「{Esc(model.AssemblyName)}」导出，{DateTime.Now:yyyy-MM-dd HH:mm} -->");
            sb.AppendLine("<!-- 约定：零位时所有连杆坐标系与 base_link 平行；单位：米、千克、弧度 -->");
            if (model.UpAxis == "y") sb.AppendLine("<!-- SolidWorks 的 Y 向上已转换为 ROS 的 Z 向上 -->");
            sb.AppendLine($"<robot name=\"{model.RobotName}\">");
            foreach (var l in model.Links)
            {
                sb.AppendLine();
                sb.AppendLine($"  <!-- {Esc(l.Label)}：{Esc(string.Join("、", l.Members.Select(m => m.Name2)))} -->");
                sb.AppendLine($"  <link name=\"{l.Name}\">");
                if (l.Mass > 0)
                {
                    var I = l.Inertia;
                    sb.AppendLine("    <inertial>");
                    sb.AppendLine($"      <origin xyz=\"{V(l.Com - l.Origin)}\" rpy=\"0 0 0\"/>");
                    sb.AppendLine($"      <mass value=\"{N(l.Mass)}\"/>");
                    sb.AppendLine($"      <inertia ixx=\"{N(I[0, 0])}\" ixy=\"{N(I[0, 1])}\" ixz=\"{N(I[0, 2])}\" iyy=\"{N(I[1, 1])}\" iyz=\"{N(I[1, 2])}\" izz=\"{N(I[2, 2])}\"/>");
                    sb.AppendLine("    </inertial>");
                }
                if (meshes != null && meshes.Files.ContainsKey(l))
                {
                    foreach (var tag in new[] { "visual", "collision" })
                    {
                        sb.AppendLine($"    <{tag}>");
                        sb.AppendLine("      <origin xyz=\"0 0 0\" rpy=\"0 0 0\"/>");
                        sb.AppendLine($"      <geometry><mesh filename=\"{meshPath(l, tag == "collision" && meshes.CollisionFiles.ContainsKey(l))}\"/></geometry>");
                        if (tag == "visual") sb.AppendLine("      <material name=\"grey\"><color rgba=\"0.78 0.79 0.82 1\"/></material>");
                        sb.AppendLine($"    </{tag}>");
                    }
                }
                sb.AppendLine("  </link>");
            }
            foreach (var j in model.Joints)
            {
                sb.AppendLine();
                sb.AppendLine($"  <!-- {Esc(j.Parent.Label)} → {Esc(j.Child.Label)}：{Joint.TypeText(j.Type)}，来自 {Esc(j.Source)} -->");
                sb.AppendLine($"  <joint name=\"{j.Name}\" type=\"{Joint.UrdfType(j.Type)}\">");
                sb.AppendLine($"    <origin xyz=\"{V(j.Child.Origin - j.Parent.Origin)}\" rpy=\"0 0 0\"/>");
                sb.AppendLine($"    <parent link=\"{j.Parent.Name}\"/>");
                sb.AppendLine($"    <child link=\"{j.Child.Name}\"/>");
                if (j.Type != JointType.Fixed) sb.AppendLine($"    <axis xyz=\"{V(j.Axis)}\"/>");
                if (j.Type == JointType.Revolute || j.Type == JointType.Prismatic)
                    sb.AppendLine($"    <limit lower=\"{N(j.Lower)}\" upper=\"{N(j.Upper)}\" effort=\"{N(j.Effort)}\" velocity=\"{N(j.Velocity)}\"/>");
                else if (j.Type == JointType.Continuous)
                    sb.AppendLine($"    <limit effort=\"{N(j.Effort)}\" velocity=\"{N(j.Velocity)}\"/>");
                sb.AppendLine("  </joint>");
            }
            sb.AppendLine("</robot>");
            return sb.ToString();
        }

        static string Esc(string s) => (s ?? "").Replace("--", "－－").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // 同一个功能包同时支持 ROS 1（catkin）和 ROS 2（ament_cmake）：package.xml 第 3 版按 $ROS_VERSION 条件选择构建方式
        static string PackageXml(string pkg) => $@"<?xml version=""1.0""?>
<package format=""3"">
  <name>{pkg}</name>
  <version>0.1.0</version>
  <description>Robot description exported from SolidWorks by SW Toolkit</description>
  <maintainer email=""todo@example.com"">todo</maintainer>
  <license>TODO</license>

  <buildtool_depend condition=""$ROS_VERSION == 1"">catkin</buildtool_depend>
  <buildtool_depend condition=""$ROS_VERSION == 2"">ament_cmake</buildtool_depend>

  <exec_depend>robot_state_publisher</exec_depend>
  <exec_depend>joint_state_publisher_gui</exec_depend>
  <exec_depend condition=""$ROS_VERSION == 1"">rviz</exec_depend>
  <exec_depend condition=""$ROS_VERSION == 2"">rviz2</exec_depend>
  <exec_depend condition=""$ROS_VERSION == 2"">launch_ros</exec_depend>

  <export>
    <build_type condition=""$ROS_VERSION == 1"">catkin</build_type>
    <build_type condition=""$ROS_VERSION == 2"">ament_cmake</build_type>
  </export>
</package>
";

        static string CMake(string pkg) => $@"cmake_minimum_required(VERSION 3.5)
project({pkg})

# 同时支持 ROS 1（catkin_make / catkin build）和 ROS 2（colcon build）
if(""$ENV{{ROS_VERSION}}"" STREQUAL ""2"")
  find_package(ament_cmake REQUIRED)
  install(DIRECTORY urdf meshes launch rviz
    DESTINATION share/${{PROJECT_NAME}})
  ament_package()
else()
  find_package(catkin REQUIRED)
  catkin_package()
  install(DIRECTORY urdf meshes launch rviz
    DESTINATION ${{CATKIN_PACKAGE_SHARE_DESTINATION}})
endif()
";

        static string Launch(string pkg, string robot) => $@"# 用法：ros2 launch {pkg} display.launch.py
import os
from ament_index_python.packages import get_package_share_directory
from launch import LaunchDescription
from launch_ros.actions import Node


def generate_launch_description():
    share = get_package_share_directory('{pkg}')
    with open(os.path.join(share, 'urdf', '{robot}.urdf'), 'r', encoding='utf-8') as f:
        robot_description = f.read()
    return LaunchDescription([
        Node(package='robot_state_publisher', executable='robot_state_publisher',
             parameters=[{{'robot_description': robot_description}}]),
        Node(package='joint_state_publisher_gui', executable='joint_state_publisher_gui'),
        Node(package='rviz2', executable='rviz2',
             arguments=['-d', os.path.join(share, 'rviz', 'display.rviz')]),
    ])
";

        static string LaunchRos1(string pkg, string robot) => $@"<?xml version=""1.0""?>
<!-- 用法（ROS 1）：roslaunch {pkg} display.launch        不要滑条窗口：gui:=false -->
<launch>
  <arg name=""gui"" default=""true""/>
  <param name=""robot_description"" textfile=""$(find {pkg})/urdf/{robot}.urdf""/>
  <node if=""$(arg gui)"" name=""joint_state_publisher_gui"" pkg=""joint_state_publisher_gui"" type=""joint_state_publisher_gui""/>
  <node unless=""$(arg gui)"" name=""joint_state_publisher"" pkg=""joint_state_publisher"" type=""joint_state_publisher""/>
  <node name=""robot_state_publisher"" pkg=""robot_state_publisher"" type=""robot_state_publisher""/>
  <node name=""rviz"" pkg=""rviz"" type=""rviz"" args=""-d $(find {pkg})/rviz/display_ros1.rviz"" required=""true""/>
</launch>
";

        static string Rviz() => @"Panels:
  - Class: rviz_common/Displays
    Name: Displays
Visualization Manager:
  Class: """"
  Displays:
    - Class: rviz_default_plugins/Grid
      Name: Grid
      Enabled: true
      Cell Size: 0.1
    - Class: rviz_default_plugins/RobotModel
      Name: RobotModel
      Enabled: true
      Description Source: Topic
      Description Topic:
        Value: /robot_description
    - Class: rviz_default_plugins/TF
      Name: TF
      Enabled: false
  Global Options:
    Fixed Frame: base_link
    Background Color: 48; 48; 48
  Tools:
    - Class: rviz_default_plugins/Interact
    - Class: rviz_default_plugins/MoveCamera
  Views:
    Current:
      Class: rviz_default_plugins/Orbit
      Distance: 0.8
      Name: Current View
      Target Frame: base_link
";

        static string RvizRos1() => @"Panels:
  - Class: rviz/Displays
    Name: Displays
Visualization Manager:
  Class: """"
  Displays:
    - Class: rviz/Grid
      Name: Grid
      Enabled: true
      Cell Size: 0.1
      Plane Cell Count: 20
    - Class: rviz/RobotModel
      Name: RobotModel
      Enabled: true
      Robot Description: robot_description
      Visual Enabled: true
      Collision Enabled: false
    - Class: rviz/TF
      Name: TF
      Enabled: false
  Global Options:
    Fixed Frame: base_link
    Background Color: 48; 48; 48
  Tools:
    - Class: rviz/Interact
    - Class: rviz/MoveCamera
  Views:
    Current:
      Class: rviz/Orbit
      Distance: 0.8
      Name: Current View
      Target Frame: base_link
";

        static string Readme(RobotModel m, string pkg) => $@"# {pkg}

由 SW 工具箱从 SolidWorks 装配体「{m.AssemblyName}」导出。导出时的自动校验结果见 `校验报告.md`。

把这个文件夹放进工作空间的 `src` 目录。同一个包同时支持 ROS 1 和 ROS 2。

## ROS 2（Foxy / Humble / Jazzy …）

```bash
colcon build --packages-select {pkg}
source install/setup.bash
ros2 launch {pkg} display.launch.py
```

## ROS 1（Noetic / Melodic）

```bash
catkin_make            # 或 catkin build {pkg}
source devel/setup.bash
roslaunch {pkg} display.launch
```

需要 `robot_state_publisher`、`joint_state_publisher_gui` 和 rviz（ROS 桌面版自带；Noetic 上缺的话：`sudo apt install ros-noetic-robot-state-publisher ros-noetic-joint-state-publisher-gui`）。

在弹出的 joint_state_publisher_gui 里拖动滑条，确认每个关节的转动方向和范围。

## 文件

- `urdf/{m.RobotName}.urdf`：ROS 用（网格路径为 package://）
- `urdf/{m.RobotName}_standalone.urdf`：不依赖 ROS 的工具用（PyBullet、MuJoCo 导入等，网格路径为相对路径）
- `meshes/*.stl`：每个连杆一个网格，单位米，已在连杆坐标系下
";
    }
}
