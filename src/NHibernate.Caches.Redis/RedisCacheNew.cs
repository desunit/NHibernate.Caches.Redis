using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NHibernate.Cache;
using NHibernate.Util;
using ServiceStack.Common;
using ServiceStack.Redis;
using ServiceStack.Redis.Support;
using ServiceStack.Text;
using System.Net.Sockets;

namespace NHibernate.Caches.Redis
{
    public class RedisCacheNew : ICache
    {
		internal const string PrefixName = "nhib:";
		private static readonly IInternalLogger log;
		[ThreadStatic] private static HashAlgorithm hasher;

		[ThreadStatic] private static MD5 md5;
        private readonly IRedisClientsManager clientManager;
		private readonly int expiry;
        private readonly TimeSpan expiryTimeSpan;
        static readonly ObjectSerializer serializer = new ObjectSerializer();


		private readonly string region;
		private readonly string regionPrefix = "";
	    private readonly bool noLingeringDelete = false;

		static RedisCacheNew()
		{
			log = LoggerProvider.LoggerFor((typeof(RedisCacheNew)));
		}

        public RedisCacheNew(string regionName, IDictionary<string, string> properties, IRedisClientsManager clientManager)
        {
            this.clientManager = clientManager;
			region = regionName;
			expiry = 60 * 60;
            expiryTimeSpan = TimeSpan.FromSeconds(expiry);

			if (properties != null)
			{
				var expirationString = GetExpirationString(properties);
				if (expirationString != null)
				{
					expiry = Convert.ToInt32(expirationString);
                    expiryTimeSpan = TimeSpan.FromSeconds(expiry);
					if (log.IsDebugEnabled)
					{
						log.DebugFormat("using expiration of {0} seconds", expiry);
					}
				}

				if (properties.ContainsKey("regionPrefix"))
				{
					regionPrefix = properties["regionPrefix"];

					if (log.IsDebugEnabled)
					{
						log.DebugFormat("new regionPrefix :{0}", regionPrefix);
					}
				}
				else
				{
					if (log.IsDebugEnabled)
					{
						log.Debug("no regionPrefix value given, using defaults");
					}
				}
			}
		}

		private static string GetExpirationString(IDictionary<string, string> props)
		{
			string result;
			if (!props.TryGetValue("expiration", out result))
			{
				props.TryGetValue(Cfg.Environment.CacheDefaultExpiration, out result);
			}
			return result;
		}

		private static HashAlgorithm Hasher
		{
			get
			{
				if (hasher == null)
				{
					hasher = HashAlgorithm.Create();
				}
				return hasher;
			}
		}

		private static MD5 Md5
		{
			get
			{
				if (md5 == null)
				{
					md5 = MD5.Create();
				}
				return md5;
			}
		}

		#region ICache Members

		public object Get(object key)
		{
			if (key == null)
			{
				return null;
			}
			if (log.IsDebugEnabled)
			{
				log.DebugFormat("fetching object {0} from the cache", key);
			}

            byte [] result = null;
            using (var client = clientManager.GetClient())
            {
                using (var tran = client.CreateTransaction())
                {
                    var objectKey = KeyAsString(key);
                    tran.QueueCommand(t => ((IRedisNativeClient)t).Get(objectKey), s => result = s);
                    tran.QueueCommand(t => t.ExpireEntryIn(objectKey, expiryTimeSpan));
                    tran.Commit();
                }
            }

            if (result == null)
            {
                return null;
            }

            var de = (DictionaryEntry)serializer.Deserialize(result);

		    //we need to check here that the key that we stored is really the key that we got
			//the reason is that for long keys, we hash the value, and this mean that we may get
			//hash collisions. The chance is very low, but it is better to be safe
			string checkKeyHash = GetAlternateKeyHash(key);
			if (checkKeyHash.Equals(de.Key))
			{
				return de.Value;
			}
			else
			{
				return null;
			}
		}

		public void Put(object key, object value)
		{
			if (key == null)
			{
				throw new ArgumentNullException("key", "null key not allowed");
			}
			if (value == null)
			{
				throw new ArgumentNullException("value", "null value not allowed");
			}

			if (log.IsDebugEnabled)
			{
				log.DebugFormat("setting value for item {0}", key);
			}

            using (var client = clientManager.GetClient())
            {
                bool returnOk = client.Set(KeyAsString(key), serializer.Serialize(new DictionaryEntry(GetAlternateKeyHash(key), value)),
                                           expiryTimeSpan);
                if (!returnOk)
                {
                    if (log.IsWarnEnabled)
                    {
                        log.WarnFormat("could not save: {0} => {1}", key, value);
                    }
                }
            }
		}

		public void Remove(object key)
		{
			if (key == null)
			{
				throw new ArgumentNullException("key");
			}
			if (log.IsDebugEnabled)
			{
				log.DebugFormat("removing item {0}", key);
			}

            using (var client = clientManager.GetClient())
            {
                client.Remove(KeyAsString(key)); 
            }
		}

		public void Clear()
		{
            using (var client = clientManager.GetClient())
            {
                var kl = client.SearchKeys(String.Concat(PrefixName, regionPrefix, region, ":*"));
                client.RemoveAll(kl);
            }
		}

		public void Destroy()
		{
			Clear();
		}

		public void Lock(object key)
		{
			// do nothing
		}

		public void Unlock(object key)
		{
			// do nothing
		}

		public long NextTimestamp()
		{
			return Timestamper.Next();
		}

		public int Timeout
		{
			get { return Timestamper.OneMs * 60000; }
		}

		public string RegionName
		{
			get { return region; }
		}

		#endregion

		/// <summary>
		/// Turn the key obj into a string, preperably using human readable
		/// string, and if the string is too long (>=250) it will be hashed
		/// </summary>
		private string KeyAsString(object key)
		{
			var fullKey = FullKeyAsString(key);
			if (fullKey.Length >= 250) //max key size for memcache
			{
				return ComputeHash(fullKey, Hasher);
			}
			else
			{
				return fullKey;
			}
		}

		/// <summary>
		/// Turn the key object into a human readable string.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		private string FullKeyAsString(object key)
		{
            return String.Concat(PrefixName, regionPrefix, region, ":", key.ToString(), "@", key.GetHashCode());
        }

		/// <summary>
		/// Compute the hash of the full key string using the given hash algorithm
		/// </summary>
		/// <param name="fullKeyString">The full key return by call FullKeyAsString</param>
		/// <param name="hashAlgorithm">The hash algorithm used to hash the key</param>
		/// <returns>The hashed key as a string</returns>
		private static string ComputeHash(string fullKeyString, HashAlgorithm hashAlgorithm)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(fullKeyString);
			byte[] computedHash = hashAlgorithm.ComputeHash(bytes);
			return Convert.ToBase64String(computedHash);
		}

		/// <summary>
		/// Compute an alternate key hash; used as a check that the looked-up value is 
		/// in fact what has been put there in the first place.
		/// </summary>
		/// <param name="key"></param>
		/// <returns>The alternate key hash (using the MD5 algorithm)</returns>
		private string GetAlternateKeyHash(object key)
		{
			string fullKey = FullKeyAsString(key);
			if (fullKey.Length >= 250)
			{
				return ComputeHash(fullKey, Md5);
			}
			else
			{
				return fullKey;
			}
		}
    }
}