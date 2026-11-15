using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TaskDock.Unity.Tests
{
    /// <summary>The package's frames must have the same shape as the golden samples in protocol/samples.</summary>
    public class ContractTests
    {
        static JObject Sample(string name)
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Wire).Assembly).resolvedPath;
            return JObject.Parse(File.ReadAllText(Path.Combine(pkg, "..", "..", "..", "protocol", "samples", name)));
        }

        static string[] Keys(JToken o) => ((JObject)o).Properties().Select(p => p.Name).OrderBy(n => n).ToArray();

        [Test]
        public void State_frame_matches_the_sample()
        {
            var sample = Sample("state-unity.json");
            var ours = JObject.Parse(Wire.Frame("state", "unity:1:abcdef", Wire.State("Compile errors (2)", "Fatebound", "Assets/Player.cs(12): CS1002", null)));
            Assert.AreEqual(Keys(sample), Keys(ours));
            Assert.AreEqual(Keys(sample["body"]), Keys(ours["body"]));
            Assert.AreEqual(Keys(sample["src"]), Keys(ours["src"]));
            Assert.AreEqual(1, (int)ours["v"]);
        }

        [Test]
        public void Hello_sample_carries_what_the_hub_needs()
        {
            var body = Sample("hello.json")["body"];
            foreach (var k in new[] { "adapter", "instance", "pid", "match", "actions", "token" }) Assert.IsNotNull(body[k], k);
        }

        [Test]
        public void Instance_is_stable_and_ignores_slash_style_and_case()
        {
            Assert.AreEqual(Wire.Instance(8812, "D:/Projects/Fatebound"), Wire.Instance(8812, @"d:\projects\fatebound\"));
            Assert.AreNotEqual(Wire.Instance(8812, "D:/Projects/Fatebound"), Wire.Instance(8812, "D:/Projects/Other"));
            StringAssert.StartsWith("unity:8812:", Wire.Instance(8812, "D:/x"));
        }

        [Test]
        public void State_priority()
        {
            var f = new StateRules.Flags { Compiling = true, Playing = true, CompileErrors = 3 };
            Assert.AreEqual("Compiling", StateRules.Pick(f).State);
            f.Compiling = false;
            Assert.AreEqual("Playing", StateRules.Pick(f).State);
            f.Playing = false;
            Assert.AreEqual("Compile errors (3)", StateRules.Pick(f).State);
            f.CompileErrors = -1;
            Assert.AreEqual("Compile errors", StateRules.Pick(f).State);
            f.CompileErrors = 0; f.TaskName = "Baking lighting"; f.TaskProgress = 0.45f;
            var bg = StateRules.Pick(f);
            Assert.AreEqual(("Background task", 0.45), (bg.State, System.Math.Round(bg.Progress.Value, 2)));
            StringAssert.StartsWith("Baking lighting 45", bg.Detail);
            f.Building = true;
            Assert.AreEqual("Building", StateRules.Pick(f).State);
            Assert.AreEqual("Idle", StateRules.Pick(new StateRules.Flags()).State);
        }
    }
}
