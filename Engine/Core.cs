using System.Globalization;
using System.Xml.Linq;
using BlazingUtilities;
using Memorize.Other;
using MemorizeShared;
using SharedCore = MemorizeShared.Engine.Core;

namespace Memorize.Engine
{
    public static class Core
    {
        public const string Version = "1.0.1";
        private const string DataXmlRelativePath = "Data.xml";
        private const string BaseFileAddress = @"https://drive.google.com/uc?export=download&id=1S-_CV6tfMYWXuamSAyXgodKPNnVjNOoM";
        private const string UpdateFolderRelativePath = @"MemorizeAppUpdate";
        private const string UpdaterZipName = "Package.zip";
        private const string UpdaterExeName = "Memorize Updater.exe";

        private const int RandomizationThreshold = 6; //Hours
        private const int IgnoreDays = 14;
        private const int IgnoreTimesMax = 3; //The Third time a word is marked for ignorance, It is fully learned. Never practice it again.

        private static DateTime _lastFailedOrCanceledUpdateAttemptDate;

        private static string _syncAddress = "";

        private static readonly Random Rnd = new();
        
        private static readonly XmlHandler XmlHandler = new();
        public static bool UpdateAvailable { get; private set; }
        public static string UpdateSource { get; private set; } = "";

        public static List<Expression> Expressions { get; } = new();
        public static List<Lesson> Lessons { get; } = new();

        private static readonly Dictionary<string, DateTime> RandomizedExpressions = new();
        private static readonly Dictionary<string, (int, DateTime)> IgnoredExpressions = new();

        public static Dictionary<string, LessonState> LessonStates { get; } = new();
        public static Dictionary<string, ExpressionState> ExpressionStates { get; } = new();

        public static string GetUpdateFolderFullPath() => Path.Join(Path.GetTempPath(), UpdateFolderRelativePath);
        public static string GetUpdaterZipFullPath() => Path.Join(GetUpdateFolderFullPath(), UpdaterZipName);
        public static string GetUpdaterExeFullPath() => Path.Join(GetUpdateFolderFullPath(), UpdaterExeName);
        public static string GetDataXmlFullPath() =>
            Path.Join(AppDomain.CurrentDomain.BaseDirectory, DataXmlRelativePath);

        public static Task WaitAsync() => SharedCore.TaskQueue.WaitAsync();

        public static void Signal() => SharedCore.TaskQueue.Signal();

        public static bool ShouldAttemptUpdate()
        {
            if (_lastFailedOrCanceledUpdateAttemptDate == default) return true;
            var today = DateTime.Today;
            var last = new DateTime(_lastFailedOrCanceledUpdateAttemptDate.Year,
                _lastFailedOrCanceledUpdateAttemptDate.Month, _lastFailedOrCanceledUpdateAttemptDate.Day);
            return today > last; //At least one day (not necessarily 24 hours ) has passed.
        }

        public static Task SetFailedOrCanceledUpdateAttemptAsync()
        {
            _lastFailedOrCanceledUpdateAttemptDate = DateTime.Now;
            XmlHandler.GetRoot().Element(Const.AppData).Check().Element(Const.LastUpdate).Check().SetAttribute(
                Const.LastAttempt, _lastFailedOrCanceledUpdateAttemptDate.ToString(CultureInfo.InvariantCulture));
            return SaveAsync();
        }

        private static XElement SetAndCreateAppDataSection_LastUpdate()
        {
            _lastFailedOrCanceledUpdateAttemptDate = default;
            var lastUpdateEl = XmlHandler.CreateElement(Const.LastUpdate);
            var lastAttemptAtt = XmlHandler.CreateAttribute(Const.LastAttempt,
                default(DateTime).ToString(CultureInfo.InvariantCulture));
            lastUpdateEl.Add(lastAttemptAtt);
            return lastUpdateEl;
        }

        public static async Task InitAsync()
        {
            await XmlHandler.InitWithFileAsync(GetDataXmlFullPath());
            bool shouldSave = false;
            var root = XmlHandler.GetRoot();
            
            var expressionsParent = root.Element(Const.Expressions);
            if (expressionsParent == null)
            {
                expressionsParent = XmlHandler.CreateElement(Const.Expressions);
                root.Add(expressionsParent);
                shouldSave = true; //Why redundant????
            }
            var lessonsParent = root.Element(Const.Lessons);
            if (lessonsParent == null)
            {
                lessonsParent = XmlHandler.CreateElement(Const.Lessons);
                root.Add(lessonsParent);
                shouldSave = true;
            }
            var lStatesParent = root.Element(Const.LessonStates);
            if (lStatesParent == null)
            {
                lStatesParent = XmlHandler.CreateElement(Const.LessonStates);
                root.Add(lStatesParent);
                shouldSave = true;
            }
            var eStatesParent = root.Element(Const.ExpressionStates);
            if (eStatesParent == null)
            {
                eStatesParent = XmlHandler.CreateElement(Const.ExpressionStates);
                root.Add(eStatesParent);
                shouldSave = true;
            }

            var dataParent = root.Element(Const.AppData);
            if (dataParent == null)
            {
                dataParent = XmlHandler.CreateElement(Const.AppData);
                dataParent.Add(SetAndCreateAppDataSection_LastUpdate());
                root.Add(dataParent);
                shouldSave = true;
            }
            else
            {
                var lastUpdateEl = dataParent.Element(Const.LastUpdate);
                if (lastUpdateEl == null)
                {
                    dataParent.Add(SetAndCreateAppDataSection_LastUpdate());
                    shouldSave = true;
                }
                else
                {
                    _lastFailedOrCanceledUpdateAttemptDate = lastUpdateEl.GetDateTime(Const.LastAttempt);
                }
            }

            var randomizedParent = root.Element(Const.RandomizedExpressions);
            if (randomizedParent == null)
            {
                randomizedParent = XmlHandler.CreateElement(Const.RandomizedExpressions);
                root.Add(randomizedParent);
                shouldSave = true;
            }

            var ignoredParent = root.Element(Const.IgnoredExpressions);
            if (ignoredParent == null)
            {
                ignoredParent = XmlHandler.CreateElement(Const.IgnoredExpressions);
                root.Add(ignoredParent);
                shouldSave = true;
            }

            try
            {
                using HttpClient client = new HttpClient();
                var baseFileContent = await client.GetStringAsync(BaseFileAddress);
                var section = "";
                foreach (var line in baseFileContent.Split("\r\n"))
                {
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line;
                        continue;
                    }

                    switch (section)
                    {
                        case "[Update]" when line.StartsWith("LatestVersion="):
                        {
                            var latest = line.Replace("LatestVersion=", "");
                            if (latest is "") continue;
                            if (latest != Version)
                            {
                                UpdateAvailable = true;
                            }

                            break;
                        }
                        case "[Update]" when line.StartsWith("UpdateSource="):
                        {
                            UpdateSource = line.Replace("UpdateSource=", "");
                            break;
                        }
                        case $"[V{Version}]" when line.StartsWith("SyncAddress="):
                        {
                            _syncAddress = line.Replace("SyncAddress=", "");
                            break;
                        }
                    }
                }

                if (_syncAddress == "") throw new Exception();

                string syncContent = await client.GetStringAsync(_syncAddress);
                XmlHandler tempHandler = new XmlHandler();
                tempHandler.InitInMemory(syncContent);
                var tempRoot = tempHandler.GetRoot();
                var newLessonsParent = tempRoot.Element(Const.Lessons).Check();
                var newExpressionsParent = tempRoot.Element(Const.Expressions).Check();
                lessonsParent.Remove();
                expressionsParent.Remove();
                root.Add(newLessonsParent, newExpressionsParent);
                lessonsParent = newLessonsParent;
                expressionsParent = newExpressionsParent;
                shouldSave = true;
            }
            catch
            {
                // ignored
            }
            finally
            {
                LoadAllLessonsAndExpressions(lessonsParent, expressionsParent);
            }

            bool shouldSave2 = LoadLessonStates(lStatesParent);
            bool shouldSave3 = LoadExpressionStates(eStatesParent);
            bool shouldSave4 = LoadRandomizedExpressions(randomizedParent);
            bool shouldSave5 = LoadIgnoredExpressions(ignoredParent);

            if (shouldSave || shouldSave2 || shouldSave3 || shouldSave4|| shouldSave5) await SaveAsync();
        }

        private static void LoadAllLessonsAndExpressions(XElement lessonsParent, XElement expressionsParent)
        {
            Expressions.Clear();
            var expressionElements = expressionsParent.Elements().ToList();
            if (expressionElements.Count == 0) return;
            foreach (var element in expressionElements)
            {
                var id = element.GetStr(Const.Id);
                var value = element.GetStr(Const.Value);
                var meaning = element.GetStr(Const.Meaning);
                var e = new Expression(id, value, meaning);
                Expressions.Add(e);
            }

            Lessons.Clear();
            var lessonElements = lessonsParent.Elements().ToList();
            if (lessonElements.Count == 0) return;
            foreach (var element in lessonElements)
            {
                var id = element.GetStr(Const.Id);
                var title = element.GetStr(Const.Title);
                var innerTitle = element.GetStr(Const.InnerTitle);
                var expressions = new List<Expression>();
                var expressionEls = element.Element(Const.Expressions).Check().Elements().ToList();
                if (expressionEls.Count != 0)
                {
                    foreach (var eEl in expressionEls)
                    {
                        var refAtt = eEl.GetStr(Const.Ref);
                        var actual = Expressions.FirstOrDefault(e => e.Id == refAtt);
                        if (actual == null) continue; // Referring to a non-existing expression, Ignore it.
                        expressions.Add(actual);
                    }
                }

                var l = new Lesson(id, title, innerTitle, expressions);
                Lessons.Add(l);
            }
        }

        private static bool LoadLessonStates(XElement parent)
        {
            bool shouldSave = false;
            foreach (var element in parent.Elements().ToList())
            {
                var refId = element.GetStr(Const.Ref);
                if (Lessons.FirstOrDefault(l => l.Id == refId) == null)
                {
                    element.Remove();
                    shouldSave = true;
                }
                else
                {
                    var state = element.GetStr(Const.Value);
                    LessonState actual = state switch
                    {
                        "NotStudied" => LessonState.NotStudied,
                        "Studied" => LessonState.Studied,
                        "Highlighted" => LessonState.Highlighted,
                        _ => throw new Exception("Invalid lesson state")
                    };
                    LessonStates.Add(refId, actual);
                }
            }

            return shouldSave;
        }

        private static bool LoadExpressionStates(XElement parent)
        {
            bool shouldSave = false;
            foreach (var element in parent.Elements().ToList())
            {
                var refId = element.GetStr(Const.Ref);
                if (Expressions.FirstOrDefault(e => e.Id == refId) == null)
                {
                    element.Remove();
                    shouldSave = true;
                }
                else
                {
                    var state = element.GetStr(Const.Value);
                    ExpressionState actual = state switch
                    {
                        "Normal" => ExpressionState.Normal,
                        "Highlighted" => ExpressionState.Highlighted,
                        _ => throw new Exception("Invalid expression state")
                    };
                    ExpressionStates.Add(refId, actual);
                }
            }

            return shouldSave;
        }

        private static bool LoadRandomizedExpressions(XElement parent)
        {
            bool shouldSave = false;
            foreach (var elem in parent.Elements())
            {
                var refId = elem.GetStr(Const.Ref);
                if (Expressions.FirstOrDefault(e => e.Id == refId) == null)
                {
                    elem.Remove();
                    shouldSave = true;
                }
                else
                {
                    var time = elem.GetDateTime(Const.Time);
                    RandomizedExpressions.Add(refId, time);
                }
            }

            return shouldSave;
        }

        private static bool LoadIgnoredExpressions(XElement parent)
        {
            bool shouldSave = false;
            foreach (var elem in parent.Elements())
            {
                var refId = elem.GetStr(Const.Ref);
                if (Expressions.FirstOrDefault(e => e.Id == refId) == null)
                {
                    elem.Remove();
                    shouldSave = true;
                }
                else
                {
                    var time = elem.GetDateTime(Const.Time);
                    var ignoredTimes = elem.GetInt(Const.IgnoredTimes);
                    IgnoredExpressions.Add(refId, (ignoredTimes, time));
                }
            }

            return shouldSave;
        }

        public static Task ChangeLessonStateAsync(string lessonId, LessonState state)
        {
            var parent = XmlHandler.GetRoot().Element(Const.LessonStates).Check();
            XElement? target;
            string str = state switch
            {
                LessonState.NotStudied => "NotStudied",
                LessonState.Studied => "Studied",
                LessonState.Highlighted => "Highlighted",
                _ => throw new Exception("Invalid lesson state.")
            };
            if (LessonStates.ContainsKey(lessonId))
            {
                LessonStates[lessonId] = state;
                target = parent.Elements()
                    .FirstOrDefault(el => el.GetStr(Const.Ref) == lessonId);
                if (target == null) throw new Exception("Lesson state element unavailable.");
                target.SetAttribute(Const.Value, str);
            }
            else
            {
                LessonStates.Add(lessonId, state);
                target = XmlHandler.CreateElement(Const.State);
                var refAtt = XmlHandler.CreateAttribute(Const.Ref, lessonId);
                var valueAtt = XmlHandler.CreateAttribute(Const.Value, str);
                target.Add(refAtt, valueAtt);
                parent.Add(target);
            }
            return SaveAsync();
        }

        public static Task ChangeExpressionStateAsync(string expressionId, ExpressionState state)
        {
            var parent = XmlHandler.GetRoot().Element(Const.ExpressionStates).Check();
            XElement? target;
            string str = state switch
            {
                ExpressionState.Normal => "Normal",
                ExpressionState.Highlighted => "Highlighted",
                _ => throw new Exception("Invalid expression state.")
            };
            if (ExpressionStates.ContainsKey(expressionId))
            {
                ExpressionStates[expressionId] = state;
                target = parent.Elements()
                    .FirstOrDefault(el => el.GetStr(Const.Ref) == expressionId);
                if (target == null) throw new Exception("Expression state element unavailable.");
                target.SetAttribute(Const.Value, str);
            }
            else
            {
                ExpressionStates.Add(expressionId, state);
                target = XmlHandler.CreateElement(Const.State);
                var refAtt = XmlHandler.CreateAttribute(Const.Ref, expressionId);
                var valueAtt = XmlHandler.CreateAttribute(Const.Value, str);
                target.Add(refAtt, valueAtt);
                parent.Add(target);
            }
            
            return SaveAsync();
        }

        public static Task SaveAsync() => XmlHandler.SaveAsync();

        public static bool ShouldIgnore(Expression e)
        {
            if (!IgnoredExpressions.TryGetValue(e.Id, out var values)) return false;
            if (values.Item1 >= IgnoreTimesMax) return true; //Fully learned
            var now = DateTime.Now;
            return now - values.Item2 < TimeSpan.FromDays(IgnoreDays);

        }

        public static Expression? Randomize()
        {
            Expression random;
            int checkedCount = 0;
            DateTime now;
            bool alreadyRandomized;
            do
            {
                if (checkedCount == Expressions.Count) return null;
                now = DateTime.Now;
                random = Expressions[Rnd.Next(0, Expressions.Count)];
                alreadyRandomized = RandomizedExpressions.ContainsKey(random.Id);
                checkedCount++;

            } while (ShouldIgnore(random) || (alreadyRandomized && now - RandomizedExpressions[random.Id] < TimeSpan.FromHours(RandomizationThreshold)));

            var parent = XmlHandler.GetRoot().Element(Const.RandomizedExpressions).Check();
            var nowStr = now.ToString(CultureInfo.InvariantCulture);
            if (alreadyRandomized)
            {
                RandomizedExpressions[random.Id] = now;
                parent.Elements().FirstOrDefault(e => e.GetStr(Const.Ref) == random.Id).Check().SetAttribute(Const.Time, nowStr);
            }
            else
            {
                RandomizedExpressions.Add(random.Id, now);
                var refAtt = XmlHandler.CreateAttribute(Const.Ref, random.Id);
                var timeAtt = XmlHandler.CreateAttribute(Const.Time, nowStr);
                var elem = XmlHandler.CreateElement(Const.RandomizedExpression);
                elem.Add(refAtt, timeAtt);
                parent.Add(elem);
            }
            return random;
        }

        public static void IgnoreExpression(Expression e)
        {
            if (!Expressions.Contains(e)) throw new Exception("Expression does not exist in the database");
            DateTime now = DateTime.Now;
            var parent = XmlHandler.GetRoot().Element(Const.IgnoredExpressions).Check();
            if (IgnoredExpressions.TryGetValue(e.Id, out var values))
            {
                values.Item1 += 1;
                values.Item2 = now;
                var el = parent.Elements().FirstOrDefault(exp => exp.GetStr(Const.Ref) == e.Id).Check();
                el.SetAttribute(Const.IgnoredTimes, values.Item1.ToString());
                el.SetAttribute(Const.Time, now.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                IgnoredExpressions.Add(e.Id, (1, now));
                var refAtt = XmlHandler.CreateAttribute(Const.Ref, e.Id);
                var timesAtt = XmlHandler.CreateAttribute(Const.IgnoredTimes, "1");
                var timeAtt = XmlHandler.CreateAttribute(Const.Time, now.ToString(CultureInfo.InvariantCulture));
                var el = XmlHandler.CreateElement(Const.IgnoredExpression);
                el.Add(refAtt, timesAtt, timeAtt);
                parent.Add(el);
            }
        }



        public static string LessonStateRepresentation(LessonState state)
        {
            return state switch
            {
                LessonState.NotStudied => "Not Studied",
                LessonState.Studied => "Studied",
                LessonState.Highlighted => "Highlighted",
                _ => throw new Exception("Invalid lesson state")
            };
        }

        public static ValueTask OnApplicationExitAsync()
        {
            return ValueTask.CompletedTask;
        }

        public static Task<bool> AttemptLoginAsync(string userName, string password)
        {
            return Task.FromResult(false);
        }
    }
}
