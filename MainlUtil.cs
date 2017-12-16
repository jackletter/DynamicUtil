/*********************************************
 * 功能描述:以热插拔方式动态调用dll|exe(c#)|cs文件,使用缓存和程序集监视优化性能
 * 创 建 人:胡庆杰
 * 日    期:2017-2-8
 ********************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using System.Collections.Concurrent;

using System.Collections;
using System.Security.Policy;

namespace DynamicUtil
{
    /// <summary>动态调用的入口,捕捉到所有的调用异常并作为HashTable返回
    /// </summary>
    public class MainlUtil
    {
        /// <summary>选择性的开启定时删除"__dynamicutil_tmp_cache"文件夹任务
        /// </summary>
        static MainlUtil()
        {
            //如果当前应用程序域名字没有以"__prefix__flag__"开头(手动创建的),并且不是卷影复制的
            if (!AppDomain.CurrentDomain.FriendlyName.StartsWith("__prefix__flag__")
                && !AppDomain.CurrentDomain.ShadowCopyFiles)
            {
                //如果不是手动创建的应用程序域就开启定时删除缓存目录功能
                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        try
                        {
                            Task.Factory.StartNew(() => { Directory.Delete(AppDomain.CurrentDomain.BaseDirectory + "__dynamicutil_tmp_cache", true); });
                        }
                        catch { };
                        //默认半小时删一次缓存(删除缓存的频率不影响更新dll或exe的实时性,只是半小时内替换了dll或exe的话的频率高的或会累积多个缓存目录)
                        System.Threading.Thread.Sleep(30 * 60 * 1000);
                    }
                });
            }
        }

        /// <summary>每个dll或exe有一个单独的AppDomain
        /// </summary>
        private class DllManageObj
        {
            public AppDomain Domain { set; get; }
            public RemoteUtil Remote { set; get; }
        }

        /// <summary>已经为dll或exe或.cs创建了单独程序域的集合
        /// </summary>
        private static ConcurrentDictionary<string, DllManageObj> dllManagers = new ConcurrentDictionary<string, DllManageObj>();

        /// <summary>创建影子加载程序域
        /// </summary>
        /// <param name="appDomainName">新创建的程序域的名字</param>
        /// <param name="shadowSearchPath">影子加载和程序集搜索的路径</param>
        /// <returns></returns>
        private static AppDomain CreateShadowAppDomain(string appDomainName, string shadowSearchPath, string appconfigpath = null)
        {
            if (string.IsNullOrWhiteSpace(appDomainName) || appDomainName.Length > 240)
            {
                throw new Exception("应用程序域名称不能太长,也不能为空！");
            }
            string path = "";
            string[] paths = (shadowSearchPath ?? "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < paths.Length; i++)
            {
                if (!paths[i].Contains(":"))
                {
                    //不包含冒号即认为是相对路径
                    paths[i] = AppDomain.CurrentDomain.BaseDirectory + paths[i].Trim('/').Trim('\\');
                }
                path += paths[i] + ";";
            }
            if (!path.ToUpper().Replace("\\", "/").Contains(AppDomain.CurrentDomain.BaseDirectory.Trim('\\').Trim('/').ToUpper()))
            {
                //如果指定的搜索路径中不包含当前应用程序域的基目录就自动加上
                path += AppDomain.CurrentDomain.BaseDirectory + ";";
            }
            //IIS下basedirectory是站点根目录不是~/bin目录,这里吧bin目录加上
            //注意:不能使用AppDomain.CurrentDomain.SetupInformation.PrivateBinPath,会引发异常
            //path = path.Trim(';') + AppDomain.CurrentDomain.SetupInformation.PrivateBinPath;
            path = path.Trim(';') + ";" + AppDomain.CurrentDomain.BaseDirectory + "bin;";
            AppDomainSetup setup = new AppDomainSetup();
            // 必须设置应用程序域名字否则卷影复制不会正常工作
            appDomainName = "__prefix__flag__" + appDomainName;
            setup.ApplicationName = appDomainName;
            //设置搜索路径
            setup.PrivateBinPath = path;
            //复制身份证明到新创建的程序域
            Evidence adevidence = AppDomain.CurrentDomain.Evidence;
            //复制配置文件
            setup.ConfigurationFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
            if (!string.IsNullOrWhiteSpace(appconfigpath))
            {
                setup.ConfigurationFile = appconfigpath;
            }
            //复制搜索策略
            setup.PrivateBinPathProbe = AppDomain.CurrentDomain.SetupInformation.PrivateBinPathProbe;
            setup.ApplicationBase = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;

            // 创建新的程序域
            AppDomain domain = AppDomain.CreateDomain(appDomainName, adevidence, setup);

            AppDomain.CurrentDomain.AppendPrivatePath(path);

            if (AppDomain.CurrentDomain.ShadowCopyFiles)
            {
                domain.SetCachePath(AppDomain.CurrentDomain.SetupInformation.CachePath);
            }
            else
            {
                domain.SetCachePath(AppDomain.CurrentDomain.BaseDirectory + "__dynamicutil_tmp_cache");
            }
            // Shadow copy only the assemblies in the Assemblies directory.
            domain.SetShadowCopyPath(path);
            // Turn shadow copying on.
            domain.SetShadowCopyFiles();
            //给新创建的影子程序域添加处理事件(每加载一个程序集就给这个程序集添加监视(一旦这个程序集发生改变就卸载掉这个程序域,方便下次重新创建最新的影子程序域))
            domain.AssemblyLoad += AddWatch;
            return domain;
        }

        /// <summary>从影子程序域中创建代理对象
        /// </summary>
        /// <param name="domain">影子程序域</param>
        /// <returns></returns>
        private static RemoteUtil CreateRemoteUtil(AppDomain domain)
        {
            string path = typeof(MainlUtil).Assembly.CodeBase;
            RemoteUtil remote = (RemoteUtil)domain.CreateInstanceFromAndUnwrap(path, "DynamicUtil.RemoteUtil");
            return remote;
        }

        private static void AddWatch(object sender, AssemblyLoadEventArgs args)
        {
            Console.WriteLine("监视到了加载程序集:" + args.LoadedAssembly.CodeBase);
            FileSystemWatcher watch = new FileSystemWatcher();
            string fileAbsPath = args.LoadedAssembly.CodeBase.Replace(@"file:///", "");
            string fileName = fileAbsPath.Substring(fileAbsPath.LastIndexOf('/') + 1);
            string dir = fileAbsPath.Substring(0, fileAbsPath.Length - fileName.Length);

            watch.BeginInit();
            watch.Path = dir;
            watch.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Attributes;
            watch.EnableRaisingEvents = true;
            watch.Filter = fileName;
            watch.IncludeSubdirectories = false;
            watch.Changed += watch_Changed;
            watch.Deleted += watch_Deleted;
            watch.EndInit();
        }

        static void watch_Deleted(object sender, FileSystemEventArgs e)
        {
            //直接卸载并删除缓存即可,dllmanagers集合中不用清空(报异常了会自动重试5次)
            try
            {
                AppDomain.Unload(AppDomain.CurrentDomain);
            }
            catch { }
        }

        private static void watch_Changed(object sender, FileSystemEventArgs e)
        {
            //直接卸载并删除缓存即可,dllmanagers集合中不用清空(报异常了会自动重试5次)
            try
            {
                AppDomain.Unload(AppDomain.CurrentDomain);
            }
            catch { }
        }

        /// <summary>动态调用一个exe程序
        /// </summary>
        /// <param name="searchPath">程序集的搜索路径</param>
        /// <param name="exeFullName">程序集的全称如:Demo2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</param>
        /// <param name="paras">参数为字符串数组</param>
        /// <param name="counter">失败重试计数</param>
        /// <returns></returns>
        public static Hashtable InvokeExe(string searchPath, string exeFullName, string[] paras, int counter = 0, string appconfigpath=null)
        {
            Hashtable ht = null;
            try
            {
                DllManageObj obj;
                if (!dllManagers.TryGetValue(exeFullName + "[" + searchPath + "]", out obj))
                {
                    lock (typeof(MainlUtil))
                    {
                        if (!dllManagers.TryGetValue(exeFullName + "[" + searchPath + "]", out obj))
                        {
                            DllManageObj tmp = new DllManageObj();
                            tmp.Domain = CreateShadowAppDomain(DateTime.Now.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString().Replace("-", ""), searchPath, appconfigpath);
                            tmp.Remote = CreateRemoteUtil(tmp.Domain);
                            dllManagers[exeFullName + "[" + searchPath + "]"] = tmp;
                            obj = tmp;
                        }
                    }
                }

                //调用指定类的指定方法
                ht = obj.Remote.InvokeExe(exeFullName, paras);
            }
            catch (Exception ex)
            {
                if (ex is System.AppDomainUnloadedException)
                {
                    //计数5次如果都失败,就直接抛异常
                    if (counter > 5)
                    {
                        throw ex;
                    }
                    counter++;
                    DllManageObj o;
                    dllManagers.TryRemove(exeFullName + "[" + searchPath + "]", out o);
                    return InvokeExe(searchPath, exeFullName, paras, counter);
                }
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            return ht;
        }

        /// <summary>一次性动态调用一个exe程序
        /// </summary>
        /// <param name="searchPath">程序集的搜索路径</param>
        /// <param name="exeFullName">程序集的全称如:Demo2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</param>
        /// <param name="paras">参数为字符串数组</param>
        /// <returns></returns>
        public static Hashtable InvokeExe_Once(string searchPath, string exeFullName, string[] paras, string appconfigpath=null)
        {
            AppDomain domain = null;
            RemoteUtil remote = null;
            string tmpAppName = DateTime.Now.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString().Replace("-", "");
            Hashtable ht = null;
            try
            {
                domain = CreateShadowAppDomain(tmpAppName, searchPath, appconfigpath);
                remote = CreateRemoteUtil(domain);
                //调用指定类的指定方法
                ht = remote.InvokeExe(exeFullName, paras);
            }
            catch (Exception ex)
            {
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            finally
            {
                AppDomain.Unload(domain);
                try
                {
                    Directory.Delete(AppDomain.CurrentDomain.BaseDirectory + "DynamicCache/" + tmpAppName, true);
                }
                catch (Exception) { }
            }
            return ht;
        }

        /// <summary>动态调用一个程序集,创建新程序域去调用
        /// </summary>
        /// <param name="searchPath">程序集的搜索路径</param>
        /// <param name="typename">程序集的全称如:Demo2.Program, Demo2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</param>
        /// <param name="methodName">执行方法名</param>
        /// <param name="paraTypes">方法参数类型</param>
        /// <param name="paras">方法参数</param>
        /// <param name="counter">失败重试计数</param>
        /// <param name="appconfigpath">配置文件的路径</param>
        /// <returns></returns>
        public static Hashtable InvokeDll(string searchPath, string typename, string methodName, Type[] paraTypes, object[] paras, int counter = 0, string appconfigpath = null)
        {
            Hashtable ht = null;
            DllManageObj obj;
            try
            {
                if (!dllManagers.TryGetValue(typename + "[" + searchPath + "]", out obj))
                {
                    lock (typeof(MainlUtil))
                    {
                        if (!dllManagers.TryGetValue(typename + "[" + searchPath + "]", out obj))
                        {
                            DllManageObj tmp = new DllManageObj();
                            tmp.Domain = CreateShadowAppDomain(DateTime.Now.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString().Replace("-", ""), searchPath, appconfigpath);
                            tmp.Remote = CreateRemoteUtil(tmp.Domain);
                            dllManagers[typename + "[" + searchPath + "]"] = tmp;
                            obj = tmp;
                        }
                    }
                }

                //调用指定类的指定方法
                ht = obj.Remote.InvokeDll(typename, methodName, paraTypes, paras);
            }
            catch (Exception ex)
            {
                if (ex is System.AppDomainUnloadedException)
                {
                    //计数5次如果都失败,就直接抛异常
                    if (counter > 5)
                    {
                        throw ex;
                    }
                    counter++;
                    DllManageObj o;
                    dllManagers.TryRemove(typename + "[" + searchPath + "]", out o);
                    return InvokeDll(searchPath, typename, methodName, paraTypes, paras, counter, appconfigpath);
                }
                if (ex is System.IO.FileNotFoundException)
                {
                    DllManageObj o;
                    dllManagers.TryRemove(typename + "[" + searchPath + "]", out o);
                }
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            return ht;
        }

        /// <summary>只调用一次,不考虑连续调用的性能,这将会临时创建影子程序域调用结束后就卸载
        /// </summary>
        /// <param name="searchPath">程序集的搜索路径</param>
        /// <param name="dllFullName">程序集的全称如:Demo2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null</param>
        /// <param name="classFullName">执行类名</param>
        /// <param name="methodName">执行方法名</param>
        /// <param name="paraTypes">方法参数类型</param>
        /// <param name="paras">方法参数</param>
        /// <returns></returns>
        public static Hashtable InvokeDll_Once(string searchPath, string dllFullName, string classFullName, string methodName, Type[] paraTypes, object[] paras, string appconfigpath=null)
        {
            AppDomain domain = null;
            RemoteUtil remote = null;
            Hashtable ht = null;
            string tmpAppName = DateTime.Now.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString().Replace("-", "");
            try
            {
                domain = CreateShadowAppDomain(tmpAppName, searchPath, appconfigpath);
                remote = CreateRemoteUtil(domain);

                //调用指定类的指定方法
                ht = remote.InvokeDll(dllFullName, classFullName, methodName, paraTypes, paras);
            }
            catch (Exception ex)
            {
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            finally
            {
                AppDomain.Unload(domain);
                try
                {
                    Directory.Delete(AppDomain.CurrentDomain.BaseDirectory + "DynamicCache/" + tmpAppName, true);
                }
                catch (Exception) { }
            }
            return ht;
        }

        /// <summary>动态编译一个代码文件并执行
        /// </summary>
        /// <param name="searchPath">程序集的搜索路径</param>
        /// <param name="srcCodePath">源代码文件</param>
        /// <param name="classFullName">执行类名</param>
        /// <param name="methodName">执行方法名</param>
        /// <param name="paraTypes">方法参数类型</param>
        /// <param name="paras">方法参数</param>
        /// <param name="counter">失败重试计数</param>
        /// <returns></returns>
        public static Hashtable InvokeSrc(string searchPath, string srcCodePath, string classFullName, string methodName, Type[] paraTypes, object[] paras, int counter = 0, string appconfigpath = null)
        {
            Hashtable ht = null;
            DllManageObj obj;
            if (!dllManagers.TryGetValue(srcCodePath + "[" + searchPath + "]", out obj))
            {
                lock (typeof(MainlUtil))
                {
                    if (!dllManagers.TryGetValue(srcCodePath + "[" + searchPath + "]", out obj))
                    {
                        DllManageObj tmp = new DllManageObj();
                        tmp.Domain = CreateShadowAppDomain(DateTime.Now.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString().Replace("-", ""), searchPath, appconfigpath);
                        tmp.Remote = CreateRemoteUtil(tmp.Domain);
                        dllManagers[srcCodePath + "[" + searchPath + "]"] = tmp;
                        obj = tmp;
                    }
                }
            }
            try
            {
                //调用指定类的指定方法
                ht = obj.Remote.InvokeSrc(srcCodePath, classFullName, methodName, paraTypes, paras);
            }
            catch (Exception ex)
            {
                if (ex is System.Runtime.Remoting.RemotingException)
                {
                    //计数5次如果都失败,就直接抛异常
                    if (counter > 5)
                    {
                        throw ex;
                    }
                    counter++;
                    DllManageObj o;
                    dllManagers.TryRemove(srcCodePath + "[" + searchPath + "]", out o);
                    return InvokeSrc(searchPath, srcCodePath, classFullName, methodName, paraTypes, paras, counter);
                }
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            return ht;
        }

        /// <summary>一次性动态编译一个代码文件并执行
        /// </summary>
        /// <param name="searchPath">程序集的搜索路径</param>
        /// <param name="srcCodePath">源代码文件</param>
        /// <param name="classFullName">执行类名</param>
        /// <param name="methodName">执行方法名</param>
        /// <param name="paraTypes">方法参数类型</param>
        /// <param name="paras">方法参数</param>
        /// <returns></returns>
        public static Hashtable InvokeSrc_Once(string searchPath, string srcCodePath, string classFullName, string methodName, Type[] paraTypes, object[] paras, string appconfigpath = null)
        {
            Hashtable ht = null;
            AppDomain domain = null;
            RemoteUtil remote = null;
            string tmpAppName = DateTime.Now.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString().Replace("-", "");
            try
            {
                domain = CreateShadowAppDomain(tmpAppName, searchPath, appconfigpath);
                remote = CreateRemoteUtil(domain);
                //调用指定类的指定方法
                ht = remote.InvokeSrc(srcCodePath, classFullName, methodName, paraTypes, paras);
            }
            catch (Exception ex)
            {
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            finally
            {
                AppDomain.Unload(domain);
                try
                {
                    Directory.Delete(AppDomain.CurrentDomain.BaseDirectory + "DynamicCache/" + tmpAppName, true);
                }
                catch (Exception) { }
            }
            return ht;
        }

        /// <summary>在当前应用程序域中直接调用,为IIS提供,因为IIS中本身就是卷影复制的
        /// </summary>
        /// <param name="typeName">类型名,比如:"System.Web.WebPages.Razor.Configuration.RazorWebSectionGroup, System.Web.WebPages.Razor, Version=2.0.0.0, Culture=neutral, PublicKeyToken=31BF3856AD364E35"</param>
        /// <param name="methodName">方法名</param>
        /// <param name="paraTypes">参数类型数组</param>
        /// <param name="paras">参数数组</param>
        /// <returns></returns>
        public static Hashtable InvokeDll_Direct(string typeName, string methodName, Type[] paraTypes, object[] paras)
        {
            Hashtable ht = null;
            try
            {
                RemoteUtil remote = new RemoteUtil();
                //调用指定类的指定方法
                ht = remote.InvokeDll(Type.GetType(typeName), methodName, paraTypes, paras);
            }
            catch (Exception ex)
            {
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            return ht;
        }

        /// <summary>在当前应用程序域中直接编译执行,为IIS提供
        /// </summary>
        /// <param name="srcCodePath">源代码路径</param>
        /// <param name="typename">类型名称,如:"System.Web.WebPages.Razor.Configuration.RazorWebSectionGroup"</param>
        /// <param name="methodName">方法名</param>
        /// <param name="paraTypes">参数类型数组</param>
        /// <param name="paras">参数数组</param>
        /// <returns></returns>
        public static Hashtable InvokeSrc_Direct(string srcCodePath, string typename, string methodName, Type[] paraTypes, object[] paras)
        {
            Hashtable ht = null;
            try
            {
                RemoteUtil remote = new RemoteUtil();
                //调用指定类的指定方法
                ht = remote.InvokeSrc(srcCodePath, typename, methodName, paraTypes, paras);
            }
            catch (Exception ex)
            {
                ht = new Hashtable();
                ht["Success"] = false;
                ht["Data"] = ex.ToString();
            }
            return ht;
        }
    }
}