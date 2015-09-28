using System;
using System.Linq;

namespace ChainTableInterface
{
    public static class CallLogging
    {
        public static void LogStart(string proxyName, string methodName, params object[] args)
        {
            string argsDebug = string.Join(",", from a in args select BetterComparer.ToString(a));
            Console.WriteLine("Start call <{0}>.{1}({2})", proxyName, methodName, argsDebug);
        }

        public static void LogEnd<TResult>(string proxyName, string methodName, Outcome<TResult, Exception> outcome, params object[] args)
        {
            string argsDebug = string.Join(",", from a in args select BetterComparer.ToString(a));
            Console.WriteLine("End call <{0}>.{1}({2}) with outcome {3}", proxyName, methodName, argsDebug, BetterComparer.ToString(outcome));
        }
    }
}