﻿using System.Net;
using System.Numerics;
using System.Reflection;

namespace OrionClientLib.Modules.SettingsData
{
    public class SettingDetailsAttribute : Attribute
    {
        public string Name { get; private set; }
        public string Description { get; private set; }
        public bool Display { get; private set; }

        public SettingDetailsAttribute(string name, string description, bool display = true)
        {
            Name = name;
            Description = description;
            Display = display;
        }
    }

    public abstract class SettingValidatorAttribute : Attribute
    {
        public abstract bool Validate(object data);
    }

    public class ThreadValidatorAttribute : SettingValidatorAttribute
    {
        public override bool Validate(object data)
        {
            if (data == null)
            {
                return false;
            }

            if (!int.TryParse(data.ToString(), out int totalThreads))
            {
                return false;
            }

            return totalThreads >= 0 && totalThreads <= Environment.ProcessorCount;
        }
    }

    public abstract class TypeValidator : SettingValidatorAttribute
    {
        public List<ISettingInfo> Options;
    }

    public class TypeValidator<T> : TypeValidator where T : ISettingInfo
    {
        public TypeValidator()
        {
            Options = GetExtendedClasses<T>().Where(x => x.DisplaySetting).Cast<ISettingInfo>().ToList();
        }

        public override bool Validate(object data)
        {
            return Options.Contains((T)data);
        }

        private List<C> GetExtendedClasses<C>(params object[] constructorArgs)
        {
            List<C> objects = new List<C>();

            foreach (Type type in Assembly.GetAssembly(typeof(C)).GetTypes().Where(myType => myType.IsClass && !myType.IsAbstract && myType.IsSubclassOf(typeof(C))))
            {
                objects.Add((C)Activator.CreateInstance(type, constructorArgs));
            }

            return objects;
        }
    }

    public class UrlSettingValidation : SettingValidatorAttribute
    {
        public override bool Validate(object data)
        {
            if (data == null)
            {
                return false;
            }

            if (data.ToString() == "localhost")
            {
                return true;
            }

            //Check url
            if (Uri.TryCreate(data.ToString(), UriKind.Absolute, out Uri result))
            {
                return true;
            }

            //Check IP
            if (IPAddress.TryParse(data.ToString(), out IPAddress addres))
            {
                return true;
            }

            return false;
        }
    }

    public class MinMaxSettingValidation<T> : SettingValidatorAttribute where T : INumber<T>
    {
        public T Min { get; private set; }
        public T Max { get; private set; }

        public MinMaxSettingValidation(T min, T max)
        {
            Min = min;
            Max = max;
        }

        public override bool Validate(object data)
        {
            T v = (T)data;

            return v >= Min && v <= Max;
        }
    }

    public class OptionSettingValidation<T> : SettingValidatorAttribute
    {
        public T[] Options { get; private set; }

        public OptionSettingValidation(params T[] options)
        {
            Options = options;
        }

        public override bool Validate(object data)
        {
            return Options.Contains((T)data);
        }
    }
}
