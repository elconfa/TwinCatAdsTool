﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using log4net.Core;
using Newtonsoft.Json.Linq;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.JsonExtension;
using TwinCAT.TypeSystem;
using TwinCAT.TypeSystem.Generic;
using TwinCatAdsTool.Interfaces.Logging;
using TwinCatAdsTool.Interfaces.Services;
using TwinCatAdsTool.Logic.Properties;

namespace TwinCatAdsTool.Logic.Services
{
    public class PersistentVariableService : IPersistentVariableService
    {
        private readonly ILog logger =LoggerFactory.GetLogger();
        public async Task<JObject> ReadPersistentVariables(TcAdsClient client, IInstanceCollection<ISymbol> symbols)
        {
            var jobj = new JObject();
            try
            {
                if (client.IsConnected)
                {
                    var iterator = new SymbolIterator(symbols, s => s.IsPersistent && s.InstancePath.Split('.').Length == 2 && !s.InstancePath.Contains("["));

                    var variables = new Dictionary<string, List<JObject>>();
                    foreach (var symbol in iterator)
                    {
                        var splitPath = symbol.InstancePath.Split('.');
                        var globalName = splitPath.First();
                        var localName = splitPath.Last();
                        if (!variables.ContainsKey(globalName))
                        {
                            variables.Add(globalName, new List<JObject>());
                        }

                        try
                        {
                            var json = await client.ReadJson(symbol.InstancePath, force:true);
                            if(json.ContainsKey(localName))
                                variables[globalName].Add(json);
                            else
                            {
                                var innerObject = new JObject();
                                innerObject.Add(localName, json);
                                variables[globalName].Add(innerObject);
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Error(string.Format(Resources.ErrorDuringReadingVariable0InJsonFormat, symbol.InstancePath), e);
                        }

                    }

                    foreach (var element in variables)
                    {
                        var uo = new JObject();
                        foreach (var p in element.Value)
                        {
                            foreach (var up in p.Properties())
                            {
                                uo.Add(up);
                            }
                        }
                        jobj.Add(element.Key, uo);

                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(Resources.ErrorWhileReadingPersistentVariables,e);
            }

            return jobj;
        }
    }
}