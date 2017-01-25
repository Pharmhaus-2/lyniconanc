﻿using Lynicon.Models;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lynicon.Utility;
using Lynicon.Collation;
using Lynicon.Extensibility;
using Lynicon.Editors;

namespace Lynicon.Routing
{
    public class DataFetchingRouter
    {
        public static Func<Type, bool> TypeCheckExistenceBySummary = (t => false);
    }

    public class DataFetchingRouter<T> : DataFetchingRouter, IRouter where T : class, new()
    {
        IRouter target;
        public bool LazyData { get; set; }
        public Func<IRouter, RouteContext, object, IRouter> DivertOverride { get; set; }

        public DataFetchingRouter(IRouter target)
        {
            this.target = target;
            this.DivertOverride = null;
        }
        public DataFetchingRouter(IRouter target, bool lazyData, Func<IRouter, RouteContext, object, IRouter> divertOverride) : this(target)
        {
            this.LazyData = lazyData;
            this.DivertOverride = divertOverride;
        }

        public VirtualPathData GetVirtualPath(VirtualPathContext context)
        {
            return null;
        }

        public async Task RouteAsync(RouteContext context)
        {
            Dictionary<string, StringValues> qsParams = context.HttpContext.Request.Query.Keys
                .Where(key => key != null)
                .ToDictionary(key => key, key => context.HttpContext.Request.Query[key]);

            bool typeSpecified = false;

            // Deal with type restrictor query parameter
            if (qsParams.ContainsKey("$type") || qsParams.ContainsKey("$create"))
            {
                string typeSpec = qsParams.ContainsKey("$type") ? qsParams["$type"][0] : qsParams["$create"][0];
                typeSpec = typeSpec.ToLower();
                if (typeSpec.Contains("."))
                {
                    if (typeof(T).FullName.ToLower() != typeSpec)
                        return;
                }
                else
                {
                    string typeName = typeof(T).Name.ToLower();
                    if (typeName.EndsWith("content"))
                        typeName = typeName.UpToLast("content");
                    if (typeName != typeSpec && typeName + "content" != typeSpec)
                        return;
                }

                typeSpecified = true;
            }

            var specialQueryParams = new List<string> { "$filter" };
            specialQueryParams.AddRange(PagingSpec.PagingKeys);
            qsParams
                .Where(kvp => specialQueryParams.Contains(kvp.Key))
                .Do(kvp => context.RouteData.DataTokens.Add(kvp.Key, kvp.Value[0]));

            // May be pointless
            context.HttpContext.Items[RouteX.CurrentRouteDataKey] = context.RouteData;

            var data = GetData(context.RouteData);

            var ied = (DataRouteInterceptEventData)EventHub.Instance.ProcessEvent("DataRoute.Intercept", this, new DataRouteInterceptEventData
            {
                QueryStringParams = qsParams,
                Data = data,
                ContentType = typeof(T),
                RouteData = context.RouteData,
                WasHandled = false
            }).Data;

            // stop here if the request was intercepted
            if (ied.WasHandled)
            {
                if (ied.RouteData != null)
                    await target.RouteAsync(context);
                return;
            }

            var divert = this.DivertOverride ?? DataDiverter.Instance.Registered<T>();
            var divertRouter = divert(target, context, data);

            if (data == null)
            {
                // Request has specified a type so we can create knowing it won't cause a problem with
                // the case where multiple types exist on the same (or overlapping) template(s)
                if (typeSpecified && divertRouter != null)
                {
                    data = Collator.Instance.GetNew<T>(context.RouteData);
                    context.RouteData.DataTokens.Add("LynNewItem", true);
                }
                else
                    return;
            }

            context.RouteData.Values.Add("data", data);

            if (divertRouter != null)
                await divertRouter.RouteAsync(context);
            else
                await target.RouteAsync(context);
        }

        protected virtual object GetData(RouteData rd)
        {
            if (DataFetchingRouter.TypeCheckExistenceBySummary(typeof(T)))
            {
                bool isList = typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>);
                if (!isList)
                {
                    Summary summ = Collator.Instance.Get<Summary>(typeof(T), rd);
                    if (summ == null)
                        return null;
                }
            }
            if (LazyData)
                return new Lazy<T>(() => Collator.Instance.Get<T>(rd));
            else
                return Collator.Instance.Get<T>(rd);
        }
    }
}