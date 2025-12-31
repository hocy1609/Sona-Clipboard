using System;
using System.Collections.Generic;

namespace Sona_Clipboard.Services
{
    /// <summary>
    /// Simple IoC container without external dependencies.
    /// Use App.Services to access registered services.
    /// </summary>
    public class ServiceContainer
    {
        private readonly Dictionary<Type, object> _singletons = new();
        private readonly Dictionary<Type, Func<object>> _factories = new();

        public void RegisterSingleton<T>(T instance) where T : class
        {
            _singletons[typeof(T)] = instance;
        }

        public void RegisterSingleton<TInterface, TImplementation>() 
            where TInterface : class 
            where TImplementation : class, TInterface, new()
        {
            _singletons[typeof(TInterface)] = new TImplementation();
        }

        public void RegisterFactory<T>(Func<T> factory) where T : class
        {
            _factories[typeof(T)] = () => factory();
        }

        public T Get<T>() where T : class
        {
            if (_singletons.TryGetValue(typeof(T), out var singleton))
                return (T)singleton;

            if (_factories.TryGetValue(typeof(T), out var factory))
                return (T)factory();

            throw new InvalidOperationException($"Service {typeof(T).Name} not registered");
        }

        public T? TryGet<T>() where T : class
        {
            if (_singletons.TryGetValue(typeof(T), out var singleton))
                return (T)singleton;

            if (_factories.TryGetValue(typeof(T), out var factory))
                return (T)factory();

            return null;
        }
    }
}
