using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using Resemble.Structs;
using Resemble.GUIEditor;

namespace Resemble
{
    public static partial class APIBridge
    {
        /// <summary> Max request count per seconds. </summary>
        private const int mv = 9;
        /// <summary> Timout for a request without response. </summary>
        private const float timout = 10.0f;
        /// <summary> API base url. </summary>
        private const string apiUri = "https://app.resemble.ai/api/v1";

        //Execution queue for tasks (Guaranteed a maximum of tasks at the same time)
        private static Queue<Task> tasks = new Queue<Task>();
        private static List<Task> executionLoop = new List<Task>();
        private static bool receiveUpdates;

        //Functions used by the plugin.
        #region Basics Methodes
        public static void GetProjects(Callback.GetProject callback)
        {
            string uri = apiUri + "/projects/";
            EnqueueTask(uri, Task.Type.Get, (string content, Error error) =>
            {
                Project[] projects = error ? null : Project.FromJson(content);
                callback.Method.Invoke(callback.Target, new object[] { projects, error });
            });
        }

        public static void GetProject(string uuid)
        {
            string uri = apiUri + "/projects/" + uuid;
            EnqueueTask(uri, Task.Type.Get, (string content, Error error) =>
            {
                Debug.Log(content);
            });
        }

        public static void DeleteProject(Project project)
        {
            string uri = apiUri + "/projects/" + project.uuid;
            EnqueueTask(uri, Task.Type.Delete, (string content, Error error) => {});
        }

        public static void DeleteClip(string uuid)
        {
            string uri = apiUri + "/projects/" + Settings.project.uuid + "/clips/" + uuid;
            EnqueueTask(uri, Task.Type.Delete, (string content, Error error) => { });
        }

        public static void CreateProject(Project project, Callback.CreateProject callback)
        {
            string uri = apiUri + "/projects/";
            string data = "{\"data\":" + JsonUtility.ToJson(project) + "}";

            EnqueueTask(uri, data, Task.Type.Post, (string content, Error error) =>
            {
                ProjectStatus status = error ? null : JsonUtility.FromJson<ProjectStatus>(content);
                callback.Method.Invoke(callback.Target, new object[] { status, error });
            });
        }

        public static Task CreateClipSync(CreateClipData podData, Callback.Simple callback)
        {
            string uri = apiUri + "/projects/" + Settings.project.uuid + "/clips/sync";
            string data = new CreateClipRequest(podData, "x-high", false).Json();

            return EnqueueTask(uri, data, Task.Type.Post, (string content, Error error) =>
            {
                if (error)
                {
                    callback.Method.Invoke(callback.Target, new object[] { null, error });
                }
                else
                {
                    callback.Invoke(content, Error.None);
                }
            });
        }

        public static void CreateClip(CreateClipData podData, Callback.CreateClip callback)
        {
            string uri = apiUri + "/projects/" + Settings.project.uuid + "/clips";
            string data = new CreateClipRequest(podData, "high", false).Json();
            Debug.Log(uri);
            Debug.Log(data);

            EnqueueTask(uri, data, Task.Type.Post, (string content, Error error) =>
            {
                if (error)
                {
                    callback.Method.Invoke(callback.Target, new object[] { null, error });
                }
                else
                {
                    callback.Invoke(JsonUtility.FromJson<ClipStatus>(content), Error.None);
                }
            });
        }

        public static void GetClip(string uuid, Callback.GetClip callback)
        {
            string uri = apiUri + "/projects/" + Settings.project.uuid + "/clips/" + uuid;
            EnqueueTask(uri, Task.Type.Get, (string content, Error error) =>
            {
                callback.Method.Invoke(callback.Target, error ? new object[] { null, error } :
                    new object[] { JsonUtility.FromJson<ResembleClip>(content), Error.None });
            });
        }

        public static void GetClips(Callback.GetClips callback)
        {
            string uri = apiUri + "/projects/" + Settings.project.uuid + "/clips/";

            EnqueueTask(uri, Task.Type.Get, (string content, Error error) =>
            {
                if (error)
                    callback.Method.Invoke(callback.Target, new object[] { null, error });
                else
                    callback.Method.Invoke(callback.Target, new object[] { ResembleClip.FromJson(content), Error.None }) ;
            });
        }

        public static Task DownloadClip(string uri, Callback.Download callback)
        {
            return Task.DowloadTask(uri, callback);
        }

        public static void GetVoices(Callback.GetVoices callback)
        {
            EnqueueTask(apiUri + "/voices/", Task.Type.Get, (string content, Error error) => {
                callback.Method.Invoke(callback.Target, error ?
                new object[] { null, error } : 
                new object[] { Voice.FromJson(content), Error.None });});
        }

        public static void UpdateClip(string clipUUID, ClipPatch patch, Callback.Simple callback)
        {
            string uri = apiUri + "/projects/" + Settings.project.uuid + "/clips/" + clipUUID;
            string data = patch.ToJson();
            Debug.Log(uri);
            Debug.Log(data);
            EnqueueTask(uri, data, Task.Type.Patch, (string content, Error error) =>
            {
                if (error)
                    callback.Method.Invoke(callback.Target, new object[] { null, error });
                else
                    callback.Method.Invoke(callback.Target, new object[] { content, Error.None });
            });
        }

        #endregion

        #region Request execution
        /// <summary> Called at each editor frame when a task is waiting to be executed. </summary>
        public static void Update()
        {
            //Update progress bar
            int taskCount = tasks.Count + executionLoop.Count;
            float progress = 0;
            for (int i = 0; i < executionLoop.Count; i++)
                progress += ((int)executionLoop[i].status) / 2.0f;
            progress /= executionLoop.Count;
            string barText = "Remaining clips : " + taskCount;
            //EditorProgressBar.Display(barText, progress);

            //Remove completed tasks from execution loop
            int exeCount = executionLoop.Count;
            double time = EditorApplication.timeSinceStartup;
            for (int i = 0; i < exeCount; i++)
            {
                Task task = executionLoop[i];

                //Tag completed tasks waiting response for more tan 10 seconds
                if (time - task.time > timout)
                {
                    task.error = Error.Timeout;
                    task.status = Task.Status.Completed;
                }

                //Remove completed tasks
                if (task.status == Task.Status.Completed)
                {
                    executionLoop.RemoveAt(i);
                    exeCount--;
                    i--;
                }
            }

            //There's nothing left to execute, end of updates.
            if (tasks.Count == 0 && executionLoop.Count == 0)
            {
                EditorApplication.update -= Update;
                receiveUpdates = false;
                //EditorProgressBar.Clear();
                return;
            }

            //Send tasks from waiting queue to execution loop queue if fit.
            if (executionLoop.Count < mv)
            {
                int count = Mathf.Clamp(mv - executionLoop.Count, 0, tasks.Count);
                for (int i = 0; i < count; i++)
                    executionLoop.Add(tasks.Dequeue());
            }

            //Execute tasks in execution loop queue. 
            for (int i = 0; i < executionLoop.Count; i++)
            {
                Task task = executionLoop[i];
                if (task.status == Task.Status.WaitToBeExecuted)
                {
                    SendRequest(task);
                    task.status = Task.Status.WaitApiResponse;
                    task.time = EditorApplication.timeSinceStartup;
                }
            }
        }

        /// <summary> Enqueue a web request to the task list. This task will be executed as soon as possible. </summary>
        public static Task EnqueueTask(string uri, Task.Type type, Callback.Simple resultProcessor)
        {
            return EnqueueTask(uri, "", type, resultProcessor);
        }

        /// <summary> Enqueue a web request to the task list. This task will be executed as soon as possible. </summary>
        public static Task EnqueueTask(string uri, string data, Task.Type type, Callback.Simple resultProcessor)
        {
            if (type == Task.Type.Download)
            {
                Debug.LogError("Download tasks start automatically, there is no need to put them in the queue.");
                return null;
            }

            Task task = new Task(uri, data, resultProcessor, type);
            tasks.Enqueue(task);
            if (!receiveUpdates)
            {
                EditorApplication.update += Update;
                receiveUpdates = true;
            }
            return task;
        }

        /// <summary> Send a web request now. Call the callback with the response processed by the resultProcessor. </summary>
        public static void SendRequest(Task task)
        {
            UnityWebRequest request;
            switch (task.type)              //https://forum.unity.com/threads/posting-raw-json-into-unitywebrequest.397871/
            {
                default:
                case Task.Type.Get:
                    request = UnityWebRequest.Get(task.uri);
                    break;
                case Task.Type.Post:
                    request = UnityWebRequest.Put(task.uri, task.data);
                    request.method = "POST";
                    break;
                case Task.Type.Delete:
                    request = UnityWebRequest.Delete(task.uri);
                    break;
                case Task.Type.Patch:
                    request = UnityWebRequest.Put(task.uri, task.data);
                    request.method = "PATCH";
                    break;
            }

            request.SetRequestHeader("Authorization", string.Format("Token token=\"{0}\"", Settings.token));
            request.SetRequestHeader("content-type", "application/json; charset=UTF-8");
            request.SendWebRequest().completed += (asyncOp) => { CompleteAsyncOperation(asyncOp, request, task); };
        }

        /// <summary> Responses from requests are received here. The content of the response is process by the resultProcessor and then the callback is executed with the result. </summary>
        private static void CompleteAsyncOperation(AsyncOperation asyncOp, UnityWebRequest webRequest, Task task)
        {
            task.status = Task.Status.Completed;

            if (task.resultProcessor == null || task.type == Task.Type.Delete)
                return;

            //Fail - Network error
            if (webRequest.isNetworkError)
                task.resultProcessor.Invoke(webRequest.downloadHandler.text, Error.NetworkError);
            //Fail - Http error
            else if (webRequest.isHttpError)
                task.resultProcessor.Invoke(webRequest.downloadHandler.text, Error.FromJson(webRequest.responseCode, webRequest.downloadHandler.text));
            else
            {
                //Fail - Empty reponse
                if (webRequest.downloadHandler == null || string.IsNullOrEmpty(webRequest.downloadHandler.text))
                    task.resultProcessor.Invoke(null, Error.EmptyResponse);
                //Succes
                else
                    task.resultProcessor.Invoke(webRequest.downloadHandler.text, Error.None);
            }
        }
        #endregion
    }
}