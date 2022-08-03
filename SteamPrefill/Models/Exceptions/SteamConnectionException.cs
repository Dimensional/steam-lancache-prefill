﻿using System;
using System.Runtime.Serialization;

namespace SteamPrefill.Models.Exceptions
{
    [Serializable]
    public class SteamConnectionException : Exception
    {
        protected SteamConnectionException(SerializationInfo info, StreamingContext context) : base(info, context)
        {

        }

        public SteamConnectionException()
        {

        }

        public SteamConnectionException(string message) : base(message)
        {

        }

        public SteamConnectionException(string message, Exception inner) : base(message, inner)
        {

        }
    }
}