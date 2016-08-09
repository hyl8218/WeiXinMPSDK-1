﻿/*----------------------------------------------------------------
    Copyright (C) 2016 Senparc

    文件名：MemcachedContainerStrategy.cs
    文件功能描述：Memcached 容器缓存策略。


    创建标识：Senparc - 20160308

    修改标识：Senparc - 20160808
    修改描述：v0.0.2 删除 ItemCollection 属性，直接使用ContainerBag加入到缓存

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enyim.Caching;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
using Senparc.Weixin.Containers;

namespace Senparc.Weixin.Cache.Memcached
{
    public class MemcachedContainerStrategy : IContainerCacheStragegy
    {
        private MemcachedClient _cache;
        private MemcachedClientConfiguration _config;
        private static Dictionary<string, int> _serverlist;// = SiteConfig.MemcachedAddresss; TODO:全局注册配置


        #region 单例

        /// <summary>
        /// LocalCacheStrategy的构造函数
        /// </summary>
        MemcachedContainerStrategy()
        {
            _config = GetMemcachedClientConfiguration();
            _cache = new MemcachedClient(_config);
        }

        //静态LocalCacheStrategy
        public static IContainerCacheStragegy Instance
        {
            get
            {
                return Nested.instance;//返回Nested类中的静态成员instance
            }
        }

        class Nested
        {
            static Nested()
            {
            }
            //将instance设为一个初始化的LocalCacheStrategy新实例
            internal static readonly MemcachedContainerStrategy instance = new MemcachedContainerStrategy();
        }

        #endregion

        #region 配置

        private static MemcachedClientConfiguration GetMemcachedClientConfiguration()
        {
            //每次都要新建
            var config = new MemcachedClientConfiguration();
            foreach (var server in _serverlist)
            {
                config.Servers.Add(new IPEndPoint(IPAddress.Parse(server.Key), server.Value));
            }
            config.Protocol = MemcachedProtocol.Binary;

            return config;
        }

        static MemcachedContainerStrategy()
        {
            // //初始化memcache服务器池
            //SockIOPool pool = SockIOPool.GetInstance();
            ////设置Memcache池连接点服务器端。
            //pool.SetServers(serverlist);
            ////其他参数根据需要进行配置

            //pool.InitConnections = 3;
            //pool.MinConnections = 3;
            //pool.MaxConnections = 5;

            //pool.SocketConnectTimeout = 1000;
            //pool.SocketTimeout = 3000;

            //pool.MaintenanceSleep = 30;
            //pool.Failover = true;

            //pool.Nagle = false;
            //pool.Initialize();

            //cache = new MemcachedClient();
            //cache.EnableCompression = false;
            try
            {
                //config.Authentication.Type = typeof(PlainTextAuthenticator);
                //config.Authentication.Parameters["userName"] = "username";
                //config.Authentication.Parameters["password"] = "password";
                //config.Authentication.Parameters["zone"] = "zone";//domain?   ——Jeffrey 2015.10.20
                DateTime dt1 = DateTime.Now;
                var config = GetMemcachedClientConfiguration();
                var cache = new MemcachedClient(config);

                var testKey = Guid.NewGuid().ToString();
                var testValue = Guid.NewGuid().ToString();
                cache.Store(StoreMode.Set, testKey, testValue);
                var storeValue = cache.Get(testKey);
                if (storeValue as string != testValue)
                {
                    throw new Exception("MemcachedStrategy失效，没有计入缓存！");
                }
                cache.Remove(testKey);
                DateTime dt2 = DateTime.Now;

                WeixinTrace.Log(string.Format("MemcachedStrategy正常启用，启动及测试耗时：{0}ms", (dt2 - dt1).TotalMilliseconds));
            }
            catch (Exception ex)
            {
                //TODO:记录是同日志
                WeixinTrace.Log(string.Format("MemcachedStrategy静态构造函数异常：{0}", ex.Message));
            }
        }

        #endregion

        #region IContainerCacheStragegy 成员
        public string GetFinalKey(string key)
        {
            return String.Format("{0}:{1}", "SenparcWeixinContainer", key);
        }


        public void InsertToCache(string key, IBaseContainerBag value)//TODO:添加Timeout参数
        {
            if (string.IsNullOrEmpty(key) || value == null)
            {
                return;
            }

            var cacheKey = GetFinalKey(key);

            //TODO：加了绝对过期时间就会立即失效（再次获取后为null），memcache低版本的bug
            _cache.Store(StoreMode.Set, cacheKey, value, DateTime.Now.AddDays(1));

#if DEBUG
            value = _cache.Get(cacheKey) as IBaseContainerBag;
#endif
        }

        public void RemoveFromCache(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            var cacheKey = GetFinalKey(key);
            _cache.Remove(cacheKey);
        }

        public IBaseContainerBag Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            var cacheKey = GetFinalKey(key);
            return _cache.Get<IBaseContainerBag>(cacheKey);
        }

        public IDictionary<string, TBag> GetAll<TBag>() where TBag : IBaseContainerBag
        {
            throw new NotImplementedException();
        }

        public IDictionary<string, IBaseContainerBag> GetAll()
        {
            throw new NotImplementedException();
        }

        public bool CheckExisted(string key)
        {
            object value;
            if (_cache.TryGet(key, out value))
            {
                return true;
            }
            return false;
        }

        public long GetCount()
        {
            throw new NotImplementedException();//TODO:需要定义二级缓存键，从池中获取
        }

        public void Update(string key, IBaseContainerBag value)
        {
            var cacheKey = GetFinalKey(key);
            _cache.Store(StoreMode.Set, cacheKey, value, DateTime.Now.AddDays(1));
        }

        public void UpdateContainerBag(string key, IBaseContainerBag containerBag)
        {
            object value;
            if (_cache.TryGet(key, out value))
            {
                Update(key, containerBag);
            }
        }

        #endregion

        #region ICacheLock
        private static Random _rnd = new Random();

        private bool RetryLock(string resourceName, int retryCount, TimeSpan retryDelay, Func<bool> action)
        {
            int currentRetry = 0;
            int maxRetryDelay = (int)retryDelay.TotalMilliseconds;
            while (currentRetry++ < retryCount)
            {
                if (action())
                {
                    return true;//取得锁
                }
                Thread.Sleep(_rnd.Next(maxRetryDelay));
            }
            return false;
        }

        private string GetLockKey(string resourceName)
        {
            return string.Format("{0}:{1}", "Lock", resourceName);
        }

        public bool Lock(string resourceName)
        {
            return Lock(resourceName, 999, new TimeSpan(0, 0, 0, 0, 1000));
        }

        public bool Lock(string resourceName, int retryCount, TimeSpan retryDelay)
        {
            var key = GetFinalKey(resourceName);
            var successfull = RetryLock(key, retryCount /*暂时不限制*/, retryDelay, () =>
            {
                try
                {
                    if (_cache.Get(key) != null)
                    {
                        return false;//已被别人锁住，没有取得锁
                    }
                    else
                    {
                        _cache.Store(StoreMode.Set, key, new object(), new TimeSpan(0, 0, 10));//创建锁
                        return true;//取得锁
                    }
                }
                catch (Exception ex)
                {
                    WeixinTrace.Log("Memcached同步锁发生异常：" + ex.Message);
                    return false;
                }
            }
              );
            return successfull;
        }

        public void UnLock(string resourceName)
        {
            var key = GetFinalKey(resourceName);
            _cache.Remove(key);
        }

        #endregion

    }
}