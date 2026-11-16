using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Urdf
{
    public enum JointType { Fixed, Revolute, Continuous, Prismatic }

    /// <summary>
    /// 按几何推断关节的方式：
    ///  · Off：只看配合（旧行为）；
    ///  · Auto：以配合为准；没有配合的零部件按几何接触分析；配合把所有零件锁死（没有任何运动关节）时改为只按几何；
    ///  · GeometryOnly：忽略配合和固定状态，只按零件几何推断。
    /// </summary>
    public enum InferMode { Off, Auto, GeometryOnly }

    /// <summary>
    /// 一个连杆：若干彼此没有相对运动的顶层零部件。
    /// 约定：零位时所有连杆坐标系都与世界坐标轴平行，原点放在连接它的关节轴上，所以 URDF 里的 rpy 全是 0。
    /// </summary>
    public sealed class Link
    {
        public string Name;          // URDF 名称（英文）
        public string Label;         // 中文说明（主要零件名）
        public List<TopComp> Members = new List<TopComp>();
        public Vec3 Origin;          // 连杆坐标系原点（世界坐标）
        public double Mass;
        public Vec3 Com;             // 世界坐标
        public Mat3 Inertia;         // 关于质心、与世界坐标轴对齐
        public IEnumerable<PartInst> Parts => Members.SelectMany(m => m.Parts).Where(p => !p.Hidden);
        public override string ToString() => $"{Name}（{Label}）";
    }

    public sealed class Joint
    {
        public string Name;
        public JointType Type;
        /// <summary>从配合识别出的类型（用户可以在界面里改 Type，但可选项以它为准）。</summary>
        public JointType Detected;
        /// <summary>绕自身轴对称、只是能空转的零件（螺钉、铝柱、齿轮等），默认设为固定。</summary>
        public bool SpinCandidate;
        public Link Parent, Child;
        public Vec3 AxisPoint;       // 关节轴上一点（世界坐标）
        public Vec3 Axis = new Vec3(0, 0, 1);
        public double Lower, Upper, Effort, Velocity;
        public bool LimitsFromMate;
        public string Source;        // 由哪些配合推断出来
        public List<string> Notes = new List<string>();
        public bool NeedsReview;
        /// <summary>由零件几何推断（不是配合）。</summary>
        public bool Inferred;
        /// <summary>几何推断的置信度 0~1（≥ 0.5 默认启用）。</summary>
        public double Confidence = 1;
        /// <summary>几何推断的依据（中文，例如「轴 Φ5 与孔 Φ5.1 同轴，轴承 608」）。</summary>
        public string Evidence;
        public static string ConfidenceText(double c) => c >= 0.8 ? "高" : c >= 0.5 ? "中" : "低";

        public static string TypeText(JointType t)
        {
            switch (t)
            {
                case JointType.Revolute: return "转动";
                case JointType.Continuous: return "连续转动";
                case JointType.Prismatic: return "移动";
                default: return "固定";
            }
        }
        public static string UrdfType(JointType t)
        {
            switch (t)
            {
                case JointType.Revolute: return "revolute";
                case JointType.Continuous: return "continuous";
                case JointType.Prismatic: return "prismatic";
                default: return "fixed";
            }
        }
    }

    public sealed class RobotModel
    {
        public string AssemblyName;
        public string RobotName = "robot";
        public string UpAxis = "y";           // SolidWorks 模型的上方向；y 表示导出时转成 ROS 的 Z 向上
        public List<Link> Links = new List<Link>();
        public List<Joint> Joints = new List<Joint>();
        public Link Root => Links.FirstOrDefault();
        public List<string> Warnings = new List<string>();
        public List<string> Infos = new List<string>();
        /// <summary>几何上像关节、但被配合锁死的位置（只提示，不自动修改）。</summary>
        public List<string> Suggestions = new List<string>();
        /// <summary>实际采用的推断方式（Auto 可能退化成只按几何）。</summary>
        public InferMode InferUsed = InferMode.Off;
        public int InferredCount => Joints.Count(j => j.Inferred && j.Type != JointType.Fixed);

        /// <summary>按当前关节类型和轴线重新计算各连杆坐标系原点和惯性参数（在用户修改关节后调用）。</summary>
        public void RecomputeFrames()
        {
            foreach (var l in Links) ComputeMass(l);
            if (Root != null) Root.Origin = Vec3.Zero;
            foreach (var j in Joints)
            {
                var c = j.Child;
                if (j.Type == JointType.Revolute || j.Type == JointType.Continuous)
                {
                    var d = j.Axis.Normalized();
                    var anchor = c.Mass > 0 ? c.Com : Center(c);
                    c.Origin = j.AxisPoint + d * (anchor - j.AxisPoint).Dot(d);   // 质心在轴线上的投影
                }
                else c.Origin = c.Mass > 0 ? c.Com : Center(c);
            }
        }

        static Vec3 Center(Link l)
        {
            var parts = l.Parts.ToList();
            if (parts.Count == 0) return Vec3.Zero;
            var s = Vec3.Zero;
            foreach (var p in parts) s = s + (p.BoxMin + p.BoxMax) * 0.5;
            return s / parts.Count;
        }

        static void ComputeMass(Link l)
        {
            var parts = l.Parts.ToList();
            l.Mass = parts.Sum(p => p.Mass);
            if (l.Mass <= 0) { l.Com = Center(l); l.Inertia = Mat3.Zero; return; }
            var com = Vec3.Zero;
            foreach (var p in parts) com = com + p.Com * p.Mass;
            com = com / l.Mass;
            var I = Mat3.Zero;
            foreach (var p in parts) I = I + p.Inertia + Mat3.ParallelAxis(p.Mass, p.Com - com);
            l.Com = com;
            l.Inertia = I;
        }
    }
}
