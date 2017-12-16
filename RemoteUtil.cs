using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.CodeDom.Compiler;
using System.IO;
using Microsoft.CSharp;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;

namespace DynamicUtil
{
    /// <summary>影子程序域的代理对象,改类不处理调用过程的异常
    /// </summary>
    public class RemoteUtil : MarshalByRefObject
    {
        /// <summary>跨域调用使生命周期无限
        /// </summary>
        /// <returns>null</returns>
        public override object InitializeLifetimeService()
        {
            return null; // makes the object live indefinitely
        }

        /// <summary>锁
        /// </summary>
        private static ConcurrentDictionary<string, object> ht_locks = new ConcurrentDictionary<string, object>();

        /// <summary>指定路径下的文件最新标识
        /// </summary>
        private static ConcurrentDictionary<string, object> ht_last_flags = new ConcurrentDictionary<string, object>();

        /// <summary>指定路径下加载的最新程序集
        /// </summary>
        private static ConcurrentDictionary<string, object> ht_last_assem = new ConcurrentDictionary<string, object>();
        private static string GeneLastFlag(string filePath)
        {

            return "[" + File.GetLastWriteTime(filePath).ToString("yyyyMMddHHmmssfff") + "]" + filePath;
        }
        private static object GetLock(string filePath)
        {
            object objtmp;
            if (!ht_locks.TryGetValue(filePath, out objtmp))
            {
                lock (typeof(MainlUtil))
                {
                    if (!ht_locks.TryGetValue(filePath, out objtmp))
                    {
                        ht_locks.TryAdd(filePath, new object());
                    }
                    return ht_locks[filePath];
                }
            }
            else
            {
                return objtmp;
            }
        }

        /// <summary>动态调用指定路径的程序集的指定类指定方法
        /// </summary>
        /// <param name="dllPath">程序集路径</param>
        /// <param name="fullClassName">类全名</param>
        /// <param name="methodName">方法全名</param>
        /// <param name="paraTypes">方法参数类型(没有参数用"new Type[]{}"代替)</param>
        /// <param name="args">方法参数实例(没有参数用"new object[]{}"代替)</param>
        /// <returns>返回的HashTable中有两个元素一个是key为"Success"的bool,为true,代表调用成功,为false代表调用失败,另一个是key为"Data"的object,代表调用方法返回的结果或程序异常的信息</returns>
        public Hashtable InvokeDll(string dllFullName, string classFullName, string methodName, Type[] paraTypes, object[] args)
        {
            Assembly assem = Assembly.Load(dllFullName);
            return InvokeDll(assem, classFullName, methodName, paraTypes, args);
        }

        public Hashtable InvokeDll(string typename, string methodName, Type[] paraTypes, object[] args)
        {
            Type t = Type.GetType(typename);
            return InvokeDll(t, methodName, paraTypes, args);
        }

        /// <summary>动态调用指定程序集的指定类指定方法
        /// </summary>
        /// <param name="assem">指定的程序集对象</param>
        /// <param name="fullClassName">类全名</param>
        /// <param name="methodName">方法全名</param>
        /// <param name="paraTypes">方法参数类型(没有参数用"new Type[]{}"代替)</param>
        /// <param name="args">方法参数实例(没有参数用"new object[]{}"代替)</param>
        /// <returns>返回的HashTable中有两个元素一个是key为"Success"的bool,为true,代表调用成功,为false代表调用失败,另一个是key为"Data"的object,代表调用方法返回的结果或程序异常的信息</returns>
        public Hashtable InvokeDll(Assembly assem, string classFullName, string methodName, Type[] paraTypes, object[] args)
        {
            Hashtable ht = new Hashtable();
            Type t = assem.GetType(classFullName);
            if (t == null)
            {
                ht.Add("Success", false);
                ht.Add("Data", "[类:" + classFullName + "]没找到");
                return ht;
            }
            return InvokeDll(t, methodName, paraTypes, args);
        }

        /// <summary>动态调用指定类类型的指定方法
        /// </summary>
        /// <param name="t">指定的类类型</param>
        /// <param name="methodName">方法全名</param>
        /// <param name="paraTypes">方法参数类型(没有参数用"new Type[]{}"代替)</param>
        /// <param name="args">方法参数实例(没有参数用"new object[]{}"代替)</param>
        /// <returns>返回的HashTable中有两个元素一个是key为"Success"的bool,为true,代表调用成功,为false代表调用失败,另一个是key为"Data"的object,代表调用方法返回的结果或程序异常的信息</returns>
        public Hashtable InvokeDll(Type t, string methodName, Type[] paraTypes, object[] args)
        {
            Hashtable ht = new Hashtable();
            if (t == null)
            {
                ht.Add("Success", false);
                ht.Add("Data", "[类:null]没找到");
                return ht;
            }
            MethodInfo minfo = t.GetMethod(methodName, paraTypes);
            if (minfo == null)
            {
                ht.Add("Success", false);
                ht.Add("Data", "[类:" + t.FullName + "][方法:" + methodName + "]没找到");
                return ht;
            }
            object obj = Activator.CreateInstance(t);
            if (obj == null)
            {
                ht.Add("Success", false);
                ht.Add("Data", "[类:" + t.FullName + "]创建实例失败");
                return ht;
            }
            object objres = minfo.Invoke(obj, args);

            ht.Add("Success", true);
            ht.Add("Data", objres);
            return ht;
        }

        /// <summary>动态调用指定类类型的指定方法
        /// </summary>
        /// <param name="exePath">exe程序的路径</param>
        /// <param name="paras">参数为字符串数组</param>
        /// <returns>返回的HashTable中有两个元素一个是key为"Success"的bool,为true,代表调用成功,为false代表调用失败,另一个是key为"Data"的object,代表程序异常的信息</returns>
        public Hashtable InvokeExe(string exePath, string[] paras)
        {
            Assembly assem = Assembly.Load(exePath);
            MethodInfo minfo = assem.EntryPoint;
            if (minfo.GetParameters().Length == 1)
            {
                //入口函数有一个参数
                if (paras == null)
                {
                    //提供的没有参数就默认搞个空的字符串数组作为参数传进去
                    minfo.Invoke(null, new object[] { new string[] { } });
                }
                else
                {
                    //提供的有参数就直接传进去
                    minfo.Invoke(null, new object[] { paras });
                }
            }
            else
            {
                //入口函数没有参数
                minfo.Invoke(null, new object[] { });
            }
            Hashtable ht = new Hashtable();
            ht.Add("Success", true);
            return ht;
        }

        /// <summary>动态编译一个代码文件并执行
        /// </summary>
        /// <param name="srcCodePath">源代码路径</param>
        /// <param name="classFullName">调用的类全名</param>
        /// <param name="methodName">调用的方法名</param>
        /// <param name="paraTypes">方法参数类型(没有参数用"new Type[]{}"代替)</param>
        /// <param name="args">方法参数实例(没有参数用"new object[]{}"代替)</param>
        /// <returns>返回的HashTable中有两个元素一个是key为"Success"的bool,为true,代表调用成功,为false代表调用失败,另一个是key为"Data"的object,代表调用方法返回的结果或程序异常的信息</returns>
        public Hashtable InvokeSrc(string srcCodePath, string classFullName,
            string methodName,
            Type[] paraTypes,
            object[] args)
        {
            string value = GeneLastFlag(srcCodePath);
            object lockobj = GetLock(srcCodePath);
            lock (lockobj)
            {
                object tmp;
                if (ht_last_flags.TryGetValue(srcCodePath, out tmp))
                {
                    if (tmp.ToString() != value)
                    {
                        ht_last_assem[srcCodePath] = Compile(srcCodePath);
                        ht_last_flags[srcCodePath] = value;
                    }
                }
                else
                {
                    ht_last_assem.TryAdd(srcCodePath, Compile(srcCodePath));
                    ht_last_flags.TryAdd(srcCodePath, value);
                }
            }
            return InvokeDll(ht_last_assem[srcCodePath] as Assembly, classFullName, methodName, paraTypes, args);
        }

        /// <summary>编译源代码,返回编译好的程序集
        /// </summary>
        /// <param name="srcCodePath">源代码路径</param>
        /// <returns></returns>
        private Assembly Compile(string srcCodePath)
        {
            System.CodeDom.Compiler.CompilerParameters parameters = new System.CodeDom.Compiler.CompilerParameters();
            string srcCode = "";
            ParseSrc(srcCodePath, ref srcCode, parameters);
            parameters.GenerateExecutable = false;
            parameters.GenerateInMemory = true;
            using (var provider = new CSharpCodeProvider())
            {
                CompilerResults results = provider.CompileAssemblyFromSource(parameters, srcCode);
                CompilerErrorCollection errorcollection = results.Errors;
                string errorMsg = "";
                foreach (CompilerError error in errorcollection)
                {
                    if (error.IsWarning == true)
                    {
                        errorMsg = "Line: " + error.Line.ToString() + " Warning Number: " + error.ErrorNumber + " Warning Message: " + error.ErrorText + "\r\n";
                    }
                    else if (error.IsWarning == false)
                    {
                        errorMsg = "Line: " + error.Line.ToString() + " Error Number: " + error.ErrorNumber + " Error Message: " + error.ErrorText + "\r\n";
                    }
                }
                if (errorcollection.Count > 0)
                {
                    throw new Exception("[编译出错]" + errorMsg);
                }
                return results.CompiledAssembly;
            }
        }

        /// <summary>解析源代码,主要解析引用
        /// </summary>
        /// <param name="srcCodePath"></param>
        /// <param name="srcCode"></param>
        /// <param name="parameters"></param>
        private void ParseSrc(string srcCodePath, ref string srcCode, CompilerParameters parameters)
        {
            string[] lines = File.ReadAllLines(srcCodePath);
            srcCode = "";
            bool b = true;//为true表示还在解析程序集的引用
            foreach (var item in lines)
            {
                string tmp = item.Trim(' ');
                if (tmp.StartsWith("//#import"))
                {
                    if (b)
                    {
                        tmp = tmp.Substring(9).Trim(' ');
                        tmp = ParseFilePath(tmp);
                        if (!string.IsNullOrWhiteSpace(tmp))
                        {
                            parameters.ReferencedAssemblies.Add(tmp);
                        }
                    }
                }
                else
                {
                    b = false;
                    srcCode += "\r\n" + tmp;
                }
            }
        }

        public string ParseFilePath(string path)
        {
            //为空直接返回
            if (string.IsNullOrWhiteSpace(path)) { return path; }
            //包含":"表示绝对路径,直接返回
            if (path.Contains(":")) { return path; }
            path = path.Replace("/", "\\");
            //不以~或\\开头,就不用引用程序集的绝对路径
            if (!(path.StartsWith("~") || path.StartsWith("\\"))) { return path; }
            if (path.StartsWith("~"))
            {
                path = path.TrimStart('~');
            }
            if (path.StartsWith("\\"))
            {
                path = path.TrimStart('\\');
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

        }
    }
}