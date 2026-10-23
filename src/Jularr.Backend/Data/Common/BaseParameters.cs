namespace Jularr.Backend.Data.Common
{
    public abstract class BaseParameters : BaseData
    {
        internal static T CreateEmpty<T>()
        {
            if (!typeof(BaseParameters).IsAssignableFrom(typeof(T)) || GetFields(typeof(T)).Length != 0
                || typeof(T).GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException("This service requires explicit parameters.");
            }

            return (T)Activator.CreateInstance(typeof(T))!;
        }
    }
}
