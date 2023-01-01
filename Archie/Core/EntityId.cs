﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Archie
{
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public readonly int Id;

        public EntityId(int id)
        {
            Id = id;
        }

        public static implicit operator EntityId(int id)
        {
            return new EntityId(id);
        }

        public override bool Equals(object? obj)
        {
            return obj is EntityId e && Equals(e);
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public static bool operator ==(EntityId left, EntityId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(EntityId left, EntityId right)
        {
            return !(left == right);
        }

        public bool Equals(EntityId other)
        {
            return Id == other.Id;
        }

        public static EntityId ToEntityId(int id)
        {
            return new EntityId(id);
        }
    }
}
