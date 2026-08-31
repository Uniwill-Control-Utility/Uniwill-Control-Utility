using Microsoft.VisualBasic.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Resources;
using System.Windows;

namespace Uniwill_Control_Utility.Scripts.UCU_Super_Resolution
{
    //
    // This is a customised version of Magpie from https://github.com/Blinue/Magpie
    // I do not take credit for the full functionality of the code.
    //

    internal class ScaleModelManager
    {
        private readonly FileSystemWatcher scaleModelsWatcher = new();

        private ScaleModel[]? scaleModels = null;

        public event Action? ScaleModelsChanged;

        public ScaleModelManager()
        {
            LoadFromLocal();

            // ??ScaleModels.json???
            scaleModelsWatcher.Path = AppDomain.CurrentDomain.BaseDirectory;
            scaleModelsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            scaleModelsWatcher.Filter = App.SCALE_MODELS_JSON_PATH.Substring(App.SCALE_MODELS_JSON_PATH.LastIndexOf('\\') + 1);
            scaleModelsWatcher.Changed += ScaleModelsWatcher_Changed;
            scaleModelsWatcher.Deleted += ScaleModelsWatcher_Changed;
            try
            {
                scaleModelsWatcher.EnableRaisingEvents = true;
            }
            catch (FileNotFoundException e)
            {

            }
        }

        public ScaleModel[]? GetScaleModels()
        {
            return scaleModels;
        }

        public bool IsValid()
        {
            return scaleModels != null && scaleModels.Length > 0;
        }

        private void LoadFromLocal()
        {
            string json = "";
            if (File.Exists(App.SCALE_MODELS_JSON_PATH))
            {
                try
                {
                    json = File.ReadAllText(App.SCALE_MODELS_JSON_PATH);
                }
                catch (Exception e)
                {

                }
            }
            else
            {
                try
                {
                    Uri uri = new("pack://application:,,,/Assets/BuiltInScaleModels.json", UriKind.Absolute);
                    StreamResourceInfo info = Application.GetResourceStream(uri);
                    using (StreamReader reader = new(info.Stream))
                    {
                        json = reader.ReadToEnd();
                    }
                    File.WriteAllText(App.SCALE_MODELS_JSON_PATH, json);
                }
                catch (Exception e)
                {
                }
            }

            try
            {
                // ??????
                scaleModels = JsonNode.Parse(
                    json,
                    new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                    new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }
                )?.AsArray().Select(model => {
                    if (model == null)
                    {
                        throw new Exception("json ??");
                    }

                    JsonNode name = model["name"] ?? throw new Exception("??? name ??");
                    JsonNode effects = model["effects"] ?? throw new Exception("??? effects ??");

                    return new ScaleModel
                    {
                        Name = name.GetValue<string>(),
                        Effects = effects.ToJsonString()
                    };
                }).ToArray();

                if (scaleModels == null || scaleModels.Length == 0)
                {
                    throw new Exception("?? json ??");
                }
            }
            catch (Exception e)
            {
                scaleModels = null;
            }

            if (ScaleModelsChanged != null)
            {
                ScaleModelsChanged.Invoke();
            }
        }

        private void ScaleModelsWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(10);
            Application.Current.Dispatcher.Invoke(LoadFromLocal);
        }

        public class ScaleModel
        {
            public string Name { get; set; } = "";

            public string Effects { get; set; } = "";
        }
    }
}
