using System;
using System.Collections.Generic;

namespace ZeroAD.Sim
{
    public readonly struct SimCommand
    {
        public readonly uint Player;
        public readonly int Type;
        public readonly int Data;

        public SimCommand(uint player, int type, int data)
        {
            Player = player;
            Type = type;
            Data = data;
        }
    }

    public sealed class TurnManager
    {
        private readonly ComponentManager _componentManager;
        private readonly int _commandDelay;
        private readonly Queue<List<SimCommand>> _commandQueues = new();

        private uint _currentTurn;

        public uint CurrentTurn => _currentTurn;

        public TurnManager(ComponentManager componentManager, int commandDelay = 2)
        {
            _componentManager = componentManager;
            _commandDelay = Math.Max(0, commandDelay);
            for (int i = 0; i <= _commandDelay; i++)
                _commandQueues.Enqueue(new List<SimCommand>());
        }

        public void SubmitCommand(SimCommand command)
        {
            var queue = _commandQueues.Peek();
            queue.Add(command);
        }

        public void AdvanceTurn()
        {
            var commands = _commandQueues.Dequeue();
            foreach (var cmd in commands)
                ExecuteCommand(cmd);

            _commandQueues.Enqueue(new List<SimCommand>());
            _currentTurn++;
        }

        private void ExecuteCommand(SimCommand command)
        {
            switch (command.Type)
            {
                case 0:
                    _componentManager.RNG.NextDouble();
                    break;
                case 1:
                    _componentManager.RNG.NextInt(0, command.Data);
                    break;
            }
        }

        public byte[] ComputeStateHash()
        {
            return _componentManager.ComputeStateHash();
        }
    }
}
