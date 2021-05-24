//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.UART
{
    public class SAM_UART : UARTBase, IDoubleWordPeripheral, IKnownSize
    {
        public SAM_UART(Machine machine) : base(machine)
        {
            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control, new DoubleWordRegister(this)
                    .WithFlag(2, out var resetReceiver, FieldMode.Write, name: "RSTRX")
                    .WithFlag(3, out var resetTransmitter, FieldMode.Write, name: "RSTTX")

                    .WithFlag(4, out var enableReceiver, FieldMode.Write, name: "RXEN")
                    .WithFlag(5, out var disableReceiver, FieldMode.Write, name: "RXDIS")

                    .WithFlag(6, out var enableTransmitter, FieldMode.Write, name: "TXEN")
                    .WithFlag(7, out var disableTransmitter, FieldMode.Write, name: "TXDIS")
                    .WithFlag(8, out var resetStatus, FieldMode.Write, name: "RSTSTA")
                    .WithFlag(12, out var requestClear, FieldMode.Write, name: "REQCLR")

                    .WithWriteCallback((_, __) =>
                    {
                        if(resetReceiver.Value)
                        {
                            /* Clear FIFO */
                            ClearBuffer();
                            receiverEnabled = false;
                        }

                        /* Determine what to do with the Receiver */
                        if(disableReceiver.Value)
                        {
                            receiverEnabled = false;
                        }
                        else if(enableReceiver.Value)
                        {
                            receiverEnabled = true;
                        }

                        /* Determine what to do with the Transmitter */
                        if(disableTransmitter.Value || (resetTransmitter.Value && !enableTransmitter.Value))
                        {
                            transmitterEnabled = false;
                        }
                        else if(enableTransmitter.Value)
                        {
                            transmitterEnabled = true;
                        }

                        /* Determine what to do with the Status bits */
                        if (resetStatus.Value)
                        {
                            parityError = false;
                            frameError = false;
                            compareSucceeded = false;
                            overrunError = false;
                        }
                    })
                },

                {(long) Registers.Mode, new DoubleWordRegister(this)
                    .WithValueField(0, 4, valueProviderCallback: _ => 0, writeCallback: (_, value) =>
                    {
                        if (value != 0)
                        {
                            this.Log(LogLevel.Warning, "Trying to configure the device to an unsupported mode!");
                        }
                    }, name: "UART_MODE")
                    .WithTaggedFlag("FILTER", 4)
                    .WithEnumField(9, 3, out parityType, name: "PAR")
                    .WithTaggedFlag("BRSRCCK", 12)
                    .WithTag("CHMODE", 14, 2)
                },

                {(long) Registers.ChannelStatus, new DoubleWordRegister(this)
                    .WithFlag(0, out receiverReady, FieldMode.Read, name: "RXRDY")
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        return transmitterEnabled;
                    }, name: "TXRDY")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        return overrunError;
                    }, name: "OVRE")
                    .WithFlag(6, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        return frameError;
                    }, name: "FRAME")
                    .WithFlag(7, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        return parityError;
                    }, name: "PARE")
                    .WithFlag(15, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        return compareSucceeded;
                    }, name: "CMP")

                },

                {(long)Registers.InterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if (value)
                        {
                            receiverReadyIrqEnabled.Value = true;
                        }
                    }, name: "IER_RXRDY")
                    .WithFlag(1, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if (value)
                        {
                            transmitterReadyIrqEnabled.Value = true;
                        }
                    }, name: "IER_TXRDY")
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.InterruptDisable, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if (value)
                        {
                            receiverReadyIrqEnabled.Value = false;
                        }
                    }, name: "IDR_RXRDY")
                    .WithFlag(1, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if (value)
                        {
                            transmitterReadyIrqEnabled.Value = false;
                        }
                    }, name: "IDR_TXRDY")
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.InterruptMask, new DoubleWordRegister(this)
                    .WithFlag(0, out receiverReadyIrqEnabled, FieldMode.Read, name: "IMR_RXRDY")
                    .WithFlag(1, out transmitterReadyIrqEnabled, FieldMode.Read, name: "IMR_TXRDY")
                },

                {(long)Registers.ReceiveHolding, new DoubleWordRegister(this)
                    .WithValueField(0, 9, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        if (!receiverEnabled)
                        {
                            return 0;
                        }

                        if (!TryGetCharacter(out var character))
                        {
                            this.Log(LogLevel.Warning, "Trying to read data from empty receive fifo");
                        }
                        if (Count == 0)
                        {
                            receiverReady.Value = false;
                        }

                        /*
                         * Compare with VAL1 and VAL2 in UART_CMPR (0x24) 
                         * and set UART_SR.CMP if VAL1 <= character <= VAL2
                         */

                        if (compareModeEnable)
                        {
                            if ((compareVal1 <= character) && (character <= compareVal2))
                                compareSucceeded = true;
                        }


                        UpdateInterrupts();
                        return character;
                    }, name: "RXCHR")
                    .WithTaggedFlag("RXSYNH", 15)
                },

                {(long)Registers.TransmitHolding, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Write, writeCallback: (_, b) =>
                    {
                        if (!transmitterEnabled)
                        {
                            return;
                        }

                        this.TransmitCharacter((byte)b);
                        UpdateInterrupts();
                    }, name: "TXCHR")
                    .WithTaggedFlag("TXSYNH", 15)
                },
                {(long)Registers.Compare, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Write, writeCallback: (_, b) =>
                    {
                        compareVal1 = (byte)b;
                    }, name: "VAL1")
                    .WithFlag(12, FieldMode.Write, writeCallback: (_, value) =>
                    {
                        compareModeEnable = value;
                    }, name: "CMPMODE")
                    .WithTaggedFlag("CMPPAR", 14)
                    .WithValueField(16, 8, FieldMode.Write, writeCallback: (_, b) =>
                    {
                        compareVal2 = (byte)b;
                    }, name: "VAL2")
                },
            };

            registers = new DoubleWordRegisterCollection(this, registersMap);

            IRQ = new GPIO();
        }

        public uint ReadDoubleWord(long offset)
        {
            lock (innerLock)
            {
                return registers.Read(offset);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            lock (innerLock)
            {
                registers.Write(offset, value);
            }
        }

        public long Size => 0x4000;
        public GPIO IRQ { get; private set; }

        public override uint BaudRate => 115200;

        public override Bits StopBits => Bits.One;

        public override Parity ParityBit
        {
            get
            {
                switch (parityType.Value)
                {
                    case ParityTypeValues.Even:
                        return Parity.Even;
                    case ParityTypeValues.Odd:
                        return Parity.Odd;
                    case ParityTypeValues.Space:
                        return Parity.Forced0;
                    case ParityTypeValues.Mark:
                        return Parity.Forced1;
                    case ParityTypeValues.No:
                        return Parity.None;
                    default:
                        throw new ArgumentException("Invalid parity type");
                }
            }
        }

        protected override void CharWritten()
        {
            receiverReady.Value = true;
            UpdateInterrupts();
        }

        protected override void QueueEmptied()
        {
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            IRQ.Set((receiverEnabled && receiverReadyIrqEnabled.Value && receiverReady.Value) || (transmitterEnabled && transmitterReadyIrqEnabled.Value));
        }

        private readonly IFlagRegisterField receiverReady;

        private readonly IFlagRegisterField receiverReadyIrqEnabled;
        private readonly IFlagRegisterField transmitterReadyIrqEnabled;

        private readonly DoubleWordRegisterCollection registers;

        private IEnumRegisterField<ParityTypeValues> parityType;

        private bool receiverEnabled;
        private bool transmitterEnabled;

        private bool parityError;
        private bool frameError;
        private bool compareSucceeded;
        private bool overrunError;

        private bool compareModeEnable;
        private byte compareVal1;
        private byte compareVal2;

        private enum ParityTypeValues
        {
            Even = 0,
            Odd = 1,
            Space = 2,
            Mark = 3,
            No = 4,
        }

        private enum Registers
        {
            Control = 0x0,
            Mode = 0x04,
            InterruptEnable = 0x08,
            InterruptDisable = 0x0C,
            InterruptMask = 0x10,
            ChannelStatus = 0x14,
            ReceiveHolding = 0x18,
            TransmitHolding = 0x1C,
            BaudRateGenerator = 0x20,
            Compare = 0x24,
            WriteProtectionMode = 0xE4
        }
    }
}
