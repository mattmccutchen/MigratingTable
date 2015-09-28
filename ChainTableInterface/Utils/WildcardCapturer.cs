using System;

namespace ChainTableInterface
{
    // Capturer templates for the two cases that have come up so far:
    // introspecting an object, and going from a Type to a type parameter.
    // There may be other things one can do in Java that we didn't need yet.

    public abstract class WildcardCapturerBase<TResult>
    {
        readonly Type genericTypeDefinition;
        protected WildcardCapturerBase(Type genericTypeDefinition)
        {
            this.genericTypeDefinition = genericTypeDefinition;
        }

        public TResult CaptureOrDefault(object obj)
        {
            if (obj != null && obj.GetType().IsGenericType && obj.GetType().GetGenericTypeDefinition() == genericTypeDefinition)
            {
                return (TResult)GetType().GetMethod("Invoke")
                    .MakeGenericMethod(obj.GetType().GenericTypeArguments)
                    .Invoke(this, new object[] { obj });
            }
            else return default(TResult);
        }

        // Each subclass for a particular genericTypeDefinition should define an
        // abstract Invoke method with type parameters matching those of the
        // genericTypeDefinition and one parameter of the parameterized type.
        // This class exists to factor out the reflection code because we can,
        // even though we can't easily enforce that subclasses are defined
        // correctly.
    }

    public abstract class TypeCapturer<TResult>
    {
        public TResult Capture(Type type)
        {
            return (TResult)GetType().GetMethod("Invoke").MakeGenericMethod(type).Invoke(this, null);
        }
        public abstract TResult Invoke<TParam>();
    }
}
