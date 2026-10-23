using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.Core
{
    // Multipart input for contracts that carry a file: form values by constructor parameter, files as UploadedFile.
    internal static class FormDataBinder
    {
        public static bool HasFile(Type dataType) => dataType.GetProperties().Any(property => property.PropertyType == typeof(UploadedFile));

        // The largest file the contract allows, plus room for the other values.
        public static long RequestLimit(Type dataType) => dataType.GetProperties()
            .Select(property => property.GetCustomAttribute<DataFieldAttribute>()?.Field as FileField)
            .OfType<FileField>()
            .Sum(field => field.MaximumBytes) + 1024 * 1024;

        public static TData Bind<TData>(IFormCollection form)
        {
            var constructor = typeof(TData).GetConstructors().Single();
            var values = constructor.GetParameters().Select(parameter =>
            {
                if (parameter.ParameterType == typeof(UploadedFile))
                {
                    return form.Files.GetFile(parameter.Name!) is { } file ? new UploadedFile(file.FileName, file.Length, file.OpenReadStream) : null;
                }

                var text = form.TryGetValue(parameter.Name!, out var value) ? value.ToString() : null;
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                try
                {
                    return TypeDescriptor.GetConverter(type).ConvertFromString(null, CultureInfo.InvariantCulture, text);
                }
                catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
                {
                    throw new BadHttpRequestException("Form values could not be bound.");
                }
            }).ToArray();
            return (TData)constructor.Invoke(values);
        }
    }
}
