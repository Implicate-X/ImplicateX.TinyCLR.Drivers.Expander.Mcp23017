using System;
using System.Reflection;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Gpio.Provider;
using GHIElectronics.TinyCLR.Devices.I2c;
using GHIElectronics.TinyCLR.Devices.Spi;

namespace ImplicateX.TinyCLR.Drivers.Expander
{
	/// <summary>
	/// Provides GPIO control for an MCP23017 16-bit I/O expander over I2C or SPI.
	/// </summary>
	public sealed partial class Mcp23017 : IDisposable
	{
		private const byte IoConIntPolMask = 0x02;
		private const byte IoConOdrMask = 0x04;
		private const byte IoConMirrorMask = 0x40;

		/// <summary>
		/// Represents the MCP23017 8-bit port banks.
		/// </summary>
		public enum Port : byte
		{
			/// <summary>Port A (pins 0-7).</summary>
			A = 0,
			/// <summary>Port B (pins 8-15).</summary>
			B = 1
		}

		/// <summary>
		/// Represents high-level pin configuration modes.
		/// </summary>
		public enum PinMode : byte
		{
			/// <summary>Input without internal pull-up.</summary>
			Input = 0,
			/// <summary>Push-pull output.</summary>
			Output = 1,
			/// <summary>Input with internal pull-up enabled.</summary>
			InputPullUp = 2
		}

		/// <summary>
		/// Represents a digital pin logic level.
		/// </summary>
		public enum PinLevel : byte
		{
			/// <summary>Logic low.</summary>
			Low = 0,
			/// <summary>Logic high.</summary>
			High = 1
		}

		private readonly I2cDevice i2cDevice = null!;
		private readonly SpiDevice spiDevice = null!;
		private readonly GpioPin resetPin;
		private readonly bool useSpi;
		private readonly byte i2cAddress;
		private readonly byte spiAddress;

		private MethodInfo cachedI2cWriteMethod = null!;
		private MethodInfo cachedI2cReadMethod = null!;
		private MethodInfo cachedI2cWriteReadMethod = null!;

		private readonly GpioControllerProvider provider;
		private bool disposed;

		/// <summary>
		/// Initializes a new MCP23017 instance using I2C transport.
		/// </summary>
		/// <param name="device">Configured I2C device instance.</param>
		/// <param name="address">7-bit MCP23017 I2C address.</param>
		/// <param name="resetPin">Optional hardware reset pin.</param>
		/// <param name="interruptPin">Optional MCU GPIO connected to MCP23017 interrupt output.</param>
		public Mcp23017( I2cDevice device, byte address = 0x20, GpioPin resetPin = null!, GpioPin interruptPin = null! )
		{
			this.i2cDevice = device ?? throw new ArgumentNullException( nameof( device ) );
			this.i2cAddress = address;
			this.resetPin = resetPin;
			this.useSpi = false;

			this.provider = new GpioControllerProvider( this, interruptPin );
			this.Controller = GpioController.FromProvider( this.provider );

			this.SoftReset();
		}

		/// <summary>
		/// Initializes a new MCP23017 instance using SPI transport.
		/// </summary>
		/// <param name="device">Configured SPI device instance.</param>
		/// <param name="spiAddress">MCP23S17 hardware address bits (A2:A0).</param>
		/// <param name="resetPin">Optional hardware reset pin.</param>
		/// <param name="interruptPin">Optional MCU GPIO connected to MCP23S17 interrupt output.</param>
		public Mcp23017( SpiDevice device, byte spiAddress, GpioPin resetPin = null!, GpioPin interruptPin = null! )
		{
			this.spiDevice = device ?? throw new ArgumentNullException( nameof( device ) );
			this.spiAddress = spiAddress;
			this.resetPin = resetPin;
			this.useSpi = true;

			this.provider = new GpioControllerProvider( this, interruptPin );
			this.Controller = GpioController.FromProvider( this.provider );

			this.SoftReset();
		}

		/// <summary>
		/// Gets a GPIO controller backed by this expander provider.
		/// </summary>
		public GpioController Controller { get; }

		/// <summary>
		/// Gets the number of available GPIO pins.
		/// </summary>
		public int PinCount { get; } = 16;

		/// <summary>
		/// Reinitializes the device registers to the driver default state.
		/// </summary>
		/// <returns><see langword="true"/> when initialization completes.</returns>
		public bool Init()
		{
			this.SoftReset();
			return true;
		}

		/// <summary>
		/// Performs hardware reset (if configured) and then reapplies software defaults.
		/// </summary>
		public void Reset()
		{
			if( this.resetPin != null )
			{
				this.resetPin.Write( GpioPinValue.Low );
				Thread.Sleep( 10 );
				this.resetPin.Write( GpioPinValue.High );
				Thread.Sleep( 10 );
			}

			this.SoftReset();
		}

		/// <summary>
		/// Opens a single expander pin through the provider-backed controller.
		/// </summary>
		/// <param name="pinNumber">Pin index from 0 to 15.</param>
		/// <returns>The opened GPIO pin instance.</returns>
		public GpioPin OpenPin( int pinNumber )
		{
			return this.Controller.OpenPin( pinNumber );
		}

		/// <summary>
		/// Opens multiple expander pins through the provider-backed controller.
		/// </summary>
		/// <param name="pinNumbers">Pin indexes from 0 to 15.</param>
		/// <returns>An array containing opened GPIO pins.</returns>
		public GpioPin[] OpenPins( params int[] pinNumbers )

		{
			return this.Controller.OpenPins( pinNumbers );
		}

		/// <summary>
		/// Attempts to open a single expander pin.
		/// </summary>
		/// <param name="pinNumber">Pin index from 0 to 15.</param>
		/// <param name="pin">When this method returns, contains the opened pin if successful.</param>
		/// <returns><see langword="true"/> if the pin was opened; otherwise <see langword="false"/>.</returns>
		public bool TryOpenPin( int pinNumber, out GpioPin pin )
		{
			return this.Controller.TryOpenPin( pinNumber, out pin );
		}

		/// <summary>
		/// Attempts to open multiple expander pins.
		/// </summary>
		/// <param name="pins">When this method returns, contains opened pins if successful.</param>
		/// <param name="pinNumbers">Pin indexes from 0 to 15.</param>
		/// <returns><see langword="true"/> if all pins were opened; otherwise <see langword="false"/>.</returns>
		public bool TryOpenPins( out GpioPin[] pins, params int[] pinNumbers )
		{
			return this.Controller.TryOpenPins( out pins, pinNumbers );
		}

		/// <summary>
		/// Sets direction mode for a single pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="pinState">Desired pin mode.</param>
		public void SetPinMode( byte pin, Port port, PinMode pinState )
		{
			this.provider.SetDriveMode( ToPinIndex( port, pin ), this.ToDriveMode( pinState ) );
		}

		/// <summary>
		/// Sets pin directions for an entire port using a bitmask where 1 = output, 0 = input.
		/// </summary>
		/// <param name="portState">Output-state bitmask for the selected port.</param>
		/// <param name="port">Target port bank.</param>
		public void SetPortMode( byte portState, Port port )
		{
			this.provider.SetPortMode( portState, port );
		}

		/// <summary>
		/// Gets current direction state for an entire port as bitmask where 1 = output, 0 = input.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>Port mode bitmask.</returns>
		public byte GetPortMode( Port port )
		{
			return unchecked(( byte )~this.ReadRegister( Register.IODIR, port ));
		}

		/// <summary>
		/// Sets port direction and applies pull-up mode for input pins.
		/// </summary>
		/// <param name="portState">Output-state bitmask for the selected port.</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="pu">Pull-up mode; only <see cref="PinMode.InputPullUp"/> is applied.</param>
		public void SetPortMode( byte portState, Port port, PinMode pu )
		{
			this.provider.SetPortMode( portState, port, pu );
		}

		/// <summary>
		/// Sets output latch level for a single pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="pinLevel">Target logic level.</param>
		public void SetPin( byte pin, Port port, PinLevel pinLevel )
		{
			byte olat = this.GetOutputLatch( port );
			byte mask = ( byte )( 1 << pin );

			if( pinLevel == PinLevel.High )
			{
				olat |= mask;
			}
			else
			{
				olat &= ( byte )~mask;
			}

			this.SetOutputLatch( olat, port );
		}

		/// <summary>
		/// Toggles output latch state for a single pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		public void TogglePin( byte pin, Port port )
		{
			byte olat = this.GetOutputLatch( port );
			byte mask = ( byte )( 1 << pin );

			if( ( olat & mask ) == 0 )
			{
				olat |= mask;
			}
			else
			{
				olat &= ( byte )~mask;
			}

			this.SetOutputLatch( olat, port );
		}

		/// <summary>
		/// Sets pin mode and output level in sequence.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="pinState">Desired mode.</param>
		/// <param name="pinLevel">Desired level.</param>
		public void SetPin( byte pin, Port port, PinMode pinState, PinLevel pinLevel )
		{
			this.SetPinMode( pin, port, pinState );
			this.SetPin( pin, port, pinLevel );
		}

		/// <summary>
		/// Sets all output latches on the specified port to the same level.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <param name="pinLevel">Level applied to all 8 bits.</param>
		public void SetAllPins( Port port, PinLevel pinLevel )
		{
			this.SetOutputLatch( pinLevel == PinLevel.High ? ( byte )0xFF : ( byte )0x00, port );
		}

		/// <summary>
		/// Writes a raw output latch value to one port.
		/// </summary>
		/// <param name="olat">8-bit output latch value.</param>
		/// <param name="port">Target port bank.</param>
		public void SetPort( byte olat, Port port )
		{
			this.SetOutputLatch( olat, port );
		}

		/// <summary>
		/// Reads live GPIO input/output levels for one port.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>8-bit GPIO state.</returns>
		public byte GetPort( Port port )
		{
			return this.ReadRegister( Register.GPIO, port );
		}

		/// <summary>
		/// Reads the output latch register for one port.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>8-bit latch value.</returns>
		public byte GetOutputLatch( Port port )
		{
			return this.ReadRegister( Register.OLAT, port );
		}

		/// <summary>
		/// Writes output latches for both port banks.
		/// </summary>
		/// <param name="portLevelA">Port A latch value.</param>
		/// <param name="portLevelB">Port B latch value.</param>
		public void SetPort( byte portLevelA, byte portLevelB )
		{
			this.SetOutputLatch( portLevelA, Port.A );
			this.SetOutputLatch( portLevelB, Port.B );
		}

		/// <summary>
		/// Applies port direction and latch value in sequence.
		/// </summary>
		/// <param name="portState">Direction bitmask where 1 = output, 0 = input.</param>
		/// <param name="portLevel">Output latch value.</param>
		/// <param name="port">Target port bank.</param>
		public void SetPort( byte portState, byte portLevel, Port port )
		{
			this.SetPortMode( portState, port );
			this.SetOutputLatch( portLevel, port );
		}

		/// <summary>
		/// Enables or disables pull-up for a single pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="pinLevel">High to enable pull-up; low to disable.</param>
		public void SetPinPullUp( byte pin, Port port, PinLevel pinLevel )
		{
			byte gppu = this.GetPortPullUp( port );
			byte mask = ( byte )( 1 << pin );

			if( pinLevel == PinLevel.High )
			{
				gppu |= mask;
			}
			else
			{
				gppu &= ( byte )~mask;
			}

			this.SetPortPullUp( gppu, port );
		}

		/// <summary>
		/// Sets pull-up configuration for all pins on a port.
		/// </summary>
		/// <param name="pulledUpPins">Bitmask where 1 enables pull-up.</param>
		/// <param name="port">Target port bank.</param>
		public void SetPortPullUp( byte pulledUpPins, Port port )
		{
			this.WriteRegister( Register.GPPU, port, pulledUpPins );
		}

		/// <summary>
		/// Gets pull-up enable mask for a port.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>Bitmask where 1 means pull-up enabled.</returns>
		public byte GetPortPullUp( Port port )
		{
			return this.ReadRegister( Register.GPPU, port );
		}

		/// <summary>
		/// Reads a single pin state from the GPIO register.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <returns><see langword="true"/> when the pin is high; otherwise <see langword="false"/>.</returns>
		public bool GetPin( byte pin, Port port )
		{
			byte result = this.ReadRegister( Register.GPIO, port );
			result &= ( byte )( 1 << pin );
			return result != 0;
		}

		/// <summary>
		/// Configures interrupt output polarity.
		/// </summary>
		/// <param name="level">Non-zero for active-high; zero for active-low.</param>
		public void SetInterruptPinPol( byte level )
		{
			this.provider.SetInterruptPinPol( level );
		}

		/// <summary>
		/// Configures interrupt output as open-drain or active driver.
		/// </summary>
		/// <param name="openDrain">Non-zero to enable open-drain output.</param>
		public void SetIntOdr( byte openDrain )
		{
			this.provider.SetIntOdr( openDrain );
		}

		/// <summary>
		/// Enables or disables mirrored interrupt behavior between both ports.
		/// </summary>
		/// <param name="mirrored">Non-zero to mirror INTA/INTB; zero to keep separate.</param>
		public void SetIntMirror( byte mirrored )
		{
			this.provider.SetIntMirror( mirrored );
		}

		/// <summary>
		/// Enables interrupt-on-change for a single input pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		public void SetInterruptOnChangePin( byte pin, Port port )
		{
			this.provider.SetInterruptOnChangePin( pin, port );
		}

		/// <summary>
		/// Enables interrupt-on-compare-to-default for a single input pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="pinIntLevel">Default comparison level (0 = low, non-zero = high).</param>
		public void SetInterruptOnDefValDevPin( byte pin, Port port, byte pinIntLevel )
		{
			this.provider.SetInterruptOnDefValDevPin( pin, port, pinIntLevel );
		}

		/// <summary>
		/// Enables interrupt-on-change for selected input pins on a port.
		/// </summary>
		/// <param name="intOnChangePins">Bitmask of pins to enable.</param>
		/// <param name="port">Target port bank.</param>
		public void SetInterruptOnChangePort( byte intOnChangePins, Port port )
		{
			this.provider.SetInterruptOnChangePort( intOnChangePins, port );
		}

		/// <summary>
		/// Enables interrupt-on-default-value compare for selected pins on a port.
		/// </summary>
		/// <param name="intPins">Bitmask of pins to enable.</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="defVal">Default comparison bitmask.</param>
		public void SetInterruptOnDefValDevPort( byte intPins, Port port, byte defVal )
		{
			this.provider.SetInterruptOnDefValDevPort( intPins, port, defVal );
		}

		/// <summary>
		/// Disables all interrupt sources on the specified port.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		public void DeleteAllInterruptsOnPort( Port port )
		{
			this.provider.DeleteAllInterruptsOnPort( port );
		}

		/// <summary>
		/// Sets input polarity inversion for one pin.
		/// </summary>
		/// <param name="pin">Bit index within the selected port (0-7).</param>
		/// <param name="port">Target port bank.</param>
		/// <param name="ipolState">Non-zero to invert input polarity.</param>
		public void SetIpolPin( byte pin, Port port, byte ipolState )
		{
			this.provider.SetIpolPin( pin, port, ipolState != 0 );
		}

		/// <summary>
		/// Sets input polarity inversion mask for an entire port.
		/// </summary>
		/// <param name="ipol">Bitmask where 1 means inverted polarity.</param>
		/// <param name="port">Target port bank.</param>
		public void SetIpolPort( byte ipol, Port port )
		{
			this.provider.SetIpolPort( ipol, port );
		}

		/// <summary>
		/// Gets current input polarity inversion mask for a port.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>Input polarity inversion bitmask.</returns>
		public byte GetIpolPort( Port port )
		{
			return this.provider.GetIpolPort( port );
		}

		/// <summary>
		/// Gets the interrupt flag register for the specified port.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>Bitmask of pins that triggered an interrupt.</returns>
		public byte GetIntFlag( Port port )
		{
			return this.provider.GetIntFlag( port );
		}

		/// <summary>
		/// Gets the captured GPIO value recorded when interrupt was asserted.
		/// </summary>
		/// <param name="port">Target port bank.</param>
		/// <returns>Captured pin-state bitmask.</returns>
		public byte GetIntCap( Port port )
		{
			return this.provider.GetIntCap( port );
		}

		/// <summary>
		/// Placeholder for SPI clock changes when transport is externally configured.
		/// </summary>
		/// <param name="clock">Requested SPI clock frequency in Hz.</param>
		public void SetSPIClockSpeed( uint clock )
		{
		}

		/// <summary>
		/// Restores MCP23017 register defaults managed by the provider.
		/// </summary>
		public void SoftReset()
		{
			this.provider.ResetDevice();
		}

		/// <summary>
		/// Probes I2C communication health.
		/// </summary>
		/// <returns>0 when communication works; otherwise 1. Always 0 for SPI mode.</returns>
		public byte I2cConnectionError()
		{
			if( this.useSpi )
			{
				return 0;
			}

			try
			{
				_ = this.ReadRegister( Register.IODIR, Port.A );
				return 0;
			}
			catch
			{
				return 1;
			}
		}

		/// <summary>
		/// Releases controller/provider resources and unsubscribes interrupt hooks.
		/// </summary>
		public void Dispose()
		{
			if( this.disposed )
			{
				return;
			}

			this.disposed = true;

			this.Controller.Dispose();
			this.provider.Dispose();
		}

		/// <summary>
		/// Converts a MCP23017 port/pin pair to the linear pin index used by the driver (0-15).
		/// Port A maps to indices 0-7, and Port B maps to indices 8-15.
		/// </summary>
		/// <param name="port">The MCP23017 port containing the pin.</param>
		/// <param name="pin">The pin number within the selected port (0-7).</param>
		/// <returns>The absolute pin index in the combined 16-pin address space.</returns>
		private static int ToPinIndex( Port port, byte pin )
		{
			return ( port == Port.A ? 0 : 8 ) + pin;
		}

		/// <summary>
		/// Converts a register and port selection into the corresponding register address.
		/// </summary>
		/// <param name="register">Base register identifier.</param>
		/// <param name="port">Port selector used to choose the address offset.</param>
		/// <returns>Register address computed as the register index multiplied by two, with an additional offset of one for Port.B.</returns>
		private static byte ToRegisterAddress( Register register, Port port )
		{
			return ( byte )( ( ( byte )register * 2 ) + ( port == Port.B ? 1 : 0 ) );
		}

		/// <summary>
		/// Converts a `PinMode` value to the corresponding `GpioPinDriveMode`.
		/// </summary>
		/// <param name="pinState">The pin mode to convert.</param>
		/// <returns>The matching `GpioPinDriveMode`; returns `GpioPinDriveMode.Input` for values other than `PinMode.Output` and
		/// `PinMode.InputPullUp`.</returns>
		private GpioPinDriveMode ToDriveMode( PinMode pinState )
		{
			switch( pinState )
			{
				case PinMode.Output:
					return GpioPinDriveMode.Output;
				case PinMode.InputPullUp:
					return GpioPinDriveMode.InputPullUp;
				default:
					return GpioPinDriveMode.Input;
			}
		}

		/// <summary>
		/// Writes the output latch value to the OLAT register for the specified port.
		/// </summary>
		/// <param name="olat">Output latch value to write.</param>
		/// <param name="port">Port whose OLAT register is updated.</param>
		private void SetOutputLatch( byte olat, Port port )
		{
			this.WriteRegister( Register.OLAT, port, olat );
		}

		/// <summary>
		/// Reads the value of a register for the specified port.
		/// </summary>
		/// <remarks>Builds the register address from the register and port, then reads the raw register
		/// value.</remarks>
		/// <param name="register">Register to read.</param>
		/// <param name="port">Port that selects the register bank.</param>
		/// <returns>Value read from the addressed register.</returns>
		private byte ReadRegister( Register register, Port port )
		{
			return this.ReadRegisterRaw( ToRegisterAddress( register, port ) );
		}

		/// <summary>
		/// Writes a byte value to the register address resolved from the specified register and port.
		/// </summary>
		/// <param name="register">Register identifier used to resolve the target address.</param>
		/// <param name="port">Port associated with the register address resolution.</param>
		/// <param name="value">Byte value written to the resolved register address.</param>
		private void WriteRegister( Register register, Port port, byte value )
		{
			this.WriteRegisterRaw( ToRegisterAddress( register, port ), value );
		}

		/// <summary>
		/// Reads a raw byte value from the specified register using the active communication interface.
		/// </summary>
		/// <param name="register">Register address to read.</param>
		/// <returns>The byte value read from the register.</returns>
		private byte ReadRegisterRaw( byte register )
		{
			if( this.useSpi )
			{
				return this.ReadSpi( register );
			}

			return this.ReadI2c( register );
		}

		/// <summary>
		/// Writes a raw value to the specified register using the configured bus interface.
		/// </summary>
		/// <remarks>Uses SPI when configured; otherwise uses I2C.</remarks>
		/// <param name="register">Register address to write.</param>
		/// <param name="value">Byte value to write to the register.</param>
		private void WriteRegisterRaw( byte register, byte value )
		{
			if( this.useSpi )
			{
				this.WriteSpi( register, value );
				return;
			}

			this.WriteI2c( register, value );
		}

		/// <summary>
		/// Writes two raw data bytes to the specified device register using the configured communication interface.
		/// </summary>
		/// <remarks>Uses SPI when SPI mode is enabled; otherwise uses I2C.</remarks>
		/// <param name="register">Register address to write.</param>
		/// <param name="valueA">First data byte to write.</param>
		/// <param name="valueB">Second data byte to write.</param>
		private void WriteRegisterRaw( byte register, byte valueA, byte valueB )
		{
			if( this.useSpi )
			{
				this.WriteSpi( register, valueA, valueB );
				return;
			}

			this.WriteI2c( register, valueA, valueB );
		}

		/// <summary>
		/// Reads a single byte from the specified I2C register.
		/// </summary>
		/// <remarks>Attempts a combined write/read operation first, then falls back to separate write and read
		/// operations.</remarks>
		/// <param name="register">The register address to read from.</param>
		/// <returns>The byte value read from the specified register.</returns>
		/// <exception cref="NotSupportedException">Thrown when the I2C device does not support a compatible read operation.</exception>
		private byte ReadI2c( byte register )
		{
			byte[] buffer = new byte[ 1 ];
			byte[] writeBuffer = new byte[] { register };

			MethodInfo writeReadMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteReadMethod,
				[ "WriteReadPartial", "WriteRead" ],
				typeof( byte[] ),
				typeof( byte[] ) );

			if( writeReadMethod != null )
			{
				writeReadMethod.Invoke( this.i2cDevice, new object[] { writeBuffer, buffer } );
				return buffer[ 0 ];
			}

			MethodInfo writeMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteMethod,
				[ "WritePartial", "Write" ],
				typeof( byte[] ) );

			MethodInfo readMethod = this.GetI2cMethod(
				ref this.cachedI2cReadMethod,
				[ "ReadPartial", "Read" ],
				typeof( byte[] ) );

			if( writeMethod != null && readMethod != null )
			{
				writeMethod.Invoke( this.i2cDevice, new object[] { writeBuffer } );
				readMethod.Invoke( this.i2cDevice, new object[] { buffer } );
				return buffer[ 0 ];
			}

			throw new NotSupportedException( "Das I2C-Gerät unterstützt kein Lesen." );
		}

		/// <summary>
		/// Writes a register-value pair to the I2C device.
		/// </summary>
		/// <remarks>Attempts a direct write operation first, then falls back to a write-read operation if
		/// needed.</remarks>
		/// <param name="register">Register address to write.</param>
		/// <param name="value">Value to write to the register.</param>
		/// <exception cref="NotSupportedException">Thrown when the I2C device does not provide a supported write operation.</exception>
		private void WriteI2c( byte register, byte value )
		{
			byte[] buffer = new byte[] { register, value };

			MethodInfo writeMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteMethod,
				[ "WritePartial", "Write" ],
				typeof( byte[] ) );

			if( writeMethod != null )
			{
				writeMethod.Invoke( this.i2cDevice, new object[] { buffer } );
				return;
			}

			MethodInfo writeReadMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteReadMethod,
				[ "WriteReadPartial", "WriteRead" ],
				typeof( byte[] ),
				typeof( byte[] ) );

			if( writeReadMethod != null )
			{
				byte[] readBuffer = new byte[ 1 ];
				writeReadMethod.Invoke( this.i2cDevice, new object[] { buffer, readBuffer } );
				return;
			}

			throw new NotSupportedException( "The I2C device does not support writing." );
		}

		/// <summary>
		/// Writes a register address followed by two data bytes to the I2C device.
		/// </summary>
		/// <remarks>Attempts a direct write first, then falls back to a write-read call with a one-byte read buffer
		/// when direct write is unavailable.</remarks>
		/// <param name="register">Register address to write.</param>
		/// <param name="valueA">First data byte to write.</param>
		/// <param name="valueB">Second data byte to write.</param>
		/// <exception cref="NotSupportedException">Thrown when the I2C device exposes neither a write method nor a write-read method that can be used to perform the
		/// write operation.</exception>
		private void WriteI2c( byte register, byte valueA, byte valueB )
		{
			byte[] buffer = new byte[] { register, valueA, valueB };

			MethodInfo writeMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteMethod,
				[ "WritePartial", "Write" ],
				typeof( byte[] ) );

			if( writeMethod != null )
			{
				writeMethod.Invoke( this.i2cDevice, new object[] { buffer } );
				return;
			}

			MethodInfo writeReadMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteReadMethod,
				[ "WriteReadPartial", "WriteRead" ],
				typeof( byte[] ),
				typeof( byte[] ) );

			if( writeReadMethod != null )
			{
				byte[] readBuffer = new byte[ 1 ];
				writeReadMethod.Invoke( this.i2cDevice, new object[] { buffer, readBuffer } );
				return;
			}

			throw new NotSupportedException( "The I2C device does not support writing." );
		}

		/// <summary>
		/// Gets the first matching I2C device method for the specified names and parameter types, using a cache to avoid
		/// repeated reflection.
		/// </summary>
		/// <remarks>Searches method names in the provided order and stops at the first match.</remarks>
		/// <param name="cache">Cached method information to reuse when available; updated with the resolved method when found.</param>
		/// <param name="methodNames">Method names to search in order on the I2C device type.</param>
		/// <param name="parameterTypes">Parameter type signature used to resolve each candidate method.</param>
		/// <returns>The cached or resolved method information, or null when no matching method is found or the device type is
		/// unavailable.</returns>
		private MethodInfo GetI2cMethod( ref MethodInfo cache, string[] methodNames, params Type[] parameterTypes )
		{
			if( cache != null )
			{
				return cache;
			}

			Type deviceType = i2cDevice?.GetType()!;

			if( deviceType == null )
			{
				return null!;
			}

			for( int i = 0; i < methodNames.Length; i++ )
			{
				cache = deviceType.GetMethod( methodNames[ i ], parameterTypes );
				if( cache != null )
				{
					return cache;
				}
			}

			return null!;
		}

		/// <summary>
		/// Reads a byte from the specified register over SPI.
		/// </summary>
		/// <remarks>Performs a full-duplex SPI transfer using a read opcode, the register address, and a trailing
		/// byte, then returns the third byte from the received buffer.</remarks>
		/// <param name="register">Register address to read.</param>
		/// <returns>Value returned from the register read operation.</returns>
		private byte ReadSpi( byte register )
		{
			byte[] writeBuffer = [ this.GetSpiReadOpcode(), register, 0x00 ];
			byte[] readBuffer = new byte[ 3 ];

			this.spiDevice.TransferFullDuplex( writeBuffer, readBuffer );
			return readBuffer[ 2 ];
		}

		/// <summary>
		/// Writes a byte value to a device register over SPI.
		/// </summary>
		/// <remarks>Sends a three-byte frame in the order: write opcode, register, value.</remarks>
		/// <param name="register">Register address to write.</param>
		/// <param name="value">Byte value to send to the register.</param>
		private void WriteSpi( byte register, byte value )
		{
			this.spiDevice.Write( [ this.GetSpiWriteOpcode(), register, value ] );
		}

		/// <summary>
		/// Writes two data bytes to the specified register over SPI using the device write opcode.
		/// </summary>
		/// <remarks>Sends four bytes in order: write opcode, register, valueA, valueB.</remarks>
		/// <param name="register">Register address to write.</param>
		/// <param name="valueA">First data byte to send.</param>
		/// <param name="valueB">Second data byte to send.</param>
		private void WriteSpi( byte register, byte valueA, byte valueB )
		{
			this.spiDevice.Write( [ this.GetSpiWriteOpcode(), register, valueA, valueB ] );
		}

		/// <summary>
		/// Gets the SPI write opcode derived from the configured device address.
		/// </summary>
		/// <remarks>Assumes the underlying SPI address is provided in address form before the R/W bit is
		/// applied.</remarks>
		/// <returns>The SPI opcode for a write transaction, created by left-shifting the address by one bit so the R/W bit is cleared.</returns>
		private byte GetSpiWriteOpcode()
		{
			return ( byte )( this.spiAddress << 1 );
		}

		/// <summary>
		/// Builds the SPI read opcode from the configured device address.
		/// </summary>
		/// <returns>An 8-bit opcode with the address in the upper bits and the read bit set to 1 in the least significant bit
		/// position.</returns>
		private byte GetSpiReadOpcode()
		{
			return ( byte )( ( this.spiAddress << 1 ) | 0x01 );
		}

		/// <summary>
		/// Provides an IGpioControllerProvider implementation for the MCP23017, including 16-pin GPIO access, drive-mode
		/// control, and pin change notifications.
		/// </summary>
		/// <remarks>Coordinates register access with synchronization, tracks per-pin state, and processes hardware
		/// interrupts through an optional shared interrupt pin and background worker to raise value-changed
		/// handlers.</remarks>
		private sealed class GpioControllerProvider : IGpioControllerProvider, IDisposable
		{
			private const int PinCountValue = 16;

			private readonly Mcp23017 owner;
			private readonly object syncRoot = new();
			private readonly bool[] openPins = new bool[ PinCountValue ];
			private readonly GpioPinDriveMode[] driveModes = new GpioPinDriveMode[ PinCountValue ];
			private readonly GpioPinEdge[] valueChangedEdges = new GpioPinEdge[ PinCountValue ];
			private readonly GpioPinValueChangedEventHandler[] valueChangedHandlers = new GpioPinValueChangedEventHandler[ PinCountValue ];
			private readonly TimeSpan[] debounceTimeouts = new TimeSpan[ PinCountValue ];
			private readonly byte[] lastPortValues = new byte[ 2 ];
			private readonly GpioPin interruptPin;
			private readonly object interruptGate = new();
			private readonly Thread interruptWorker = null!;
			private volatile bool interruptPending;
			private volatile bool disposed;

			public GpioControllerProvider( Mcp23017 owner, GpioPin interruptPin )
			{
				this.owner = owner ?? throw new ArgumentNullException( nameof( owner ) );
				this.interruptPin = interruptPin;

				for( int i = 0; i < PinCountValue; i++ )
				{
					this.driveModes[ i ] = GpioPinDriveMode.Input;
				}

				ResetDevice();

				lastPortValues[ 0 ] = owner.ReadRegister( Register.GPIO, Port.A );
				lastPortValues[ 1 ] = owner.ReadRegister( Register.GPIO, Port.B );

				if( interruptPin != null )
				{
					interruptPin.SetDriveMode( GpioPinDriveMode.Input );
					interruptPin.ValueChanged += this.OnInterruptPinValueChanged;

					interruptWorker = new Thread( this.InterruptWorkerLoop );
					interruptWorker.Start();
				}
			}

			/// <summary>
			/// Gets the number of pins.
			/// </summary>
			public int PinCount
			{
				get { return PinCountValue; }
			}

			/// <summary>
			/// Resets the device to its default I/O and interrupt state, updates cached port state, and clears any pending
			/// interrupt flag.
			/// </summary>
			/// <remarks>Configures both ports as inputs, disables pull-up resistors and interrupt generation, clears
			/// interrupt comparison defaults and output latches, then refreshes internal snapshots and interrupt configuration
			/// caches.</remarks>
			public void ResetDevice()
			{
				lock( syncRoot )
				{
					EnsureNotDisposed();

					ConfigureInterruptHardware();

					owner.WriteRegister( Register.IODIR, Port.A, 0xFF );
					owner.WriteRegister( Register.IODIR, Port.B, 0xFF );
					owner.WriteRegister( Register.GPPU, Port.A, 0x00 );
					owner.WriteRegister( Register.GPPU, Port.B, 0x00 );
					owner.WriteRegister( Register.GPINTEN, Port.A, 0x00 );
					owner.WriteRegister( Register.GPINTEN, Port.B, 0x00 );
					owner.WriteRegister( Register.INTCON, Port.A, 0x00 );
					owner.WriteRegister( Register.INTCON, Port.B, 0x00 );
					owner.WriteRegister( Register.DEFVAL, Port.A, 0x00 );
					owner.WriteRegister( Register.DEFVAL, Port.B, 0x00 );
					owner.WriteRegister( Register.OLAT, Port.A, 0x00 );
					owner.WriteRegister( Register.OLAT, Port.B, 0x00 );

					RefreshSnapshot( Port.A );
					RefreshSnapshot( Port.B );
					RefreshInterruptConfigurationForPort( Port.A );
					RefreshInterruptConfigurationForPort( Port.B );

					lock( interruptGate )
					{
						interruptPending = false;
					}
				}
			}

			/// <summary>
			/// Opens the specified pin for use.
			/// </summary>
			/// <param name="pin">The number of the pin to open.</param>
			/// <exception cref="InvalidOperationException">The specified pin is already open.</exception>
			public void OpenPin( int pin )
			{
				ValidatePin( pin );

				lock( syncRoot )
				{
					EnsureNotDisposed();

					if( openPins[ pin ] )
					{
						throw new InvalidOperationException( $"Pin {pin} is already open." );
					}

					openPins[ pin ] = true;
				}
			}

			/// <summary>
			/// Closes the specified GPIO pin and releases its associated state.
			/// </summary>
			/// <remarks>Resets the pin to input mode, clears value-changed handlers and edge configuration, and
			/// refreshes interrupt configuration for the corresponding port.</remarks>
			/// <param name="pin">The logical pin number to close.</param>
			/// <exception cref="InvalidOperationException">The specified pin is not open.</exception>
			public void ClosePin( int pin )
			{
				ValidatePin( pin );

				lock( syncRoot )
				{
					EnsureNotDisposed();

					if( !openPins[ pin ] )
					{
						throw new InvalidOperationException( $"Pin {pin} is not open." );
					}

					openPins[ pin ] = false;
					driveModes[ pin ] = GpioPinDriveMode.Input;
					valueChangedHandlers[ pin ] = null!;
					valueChangedEdges[ pin ] = 0;

					RefreshInterruptConfigurationForPort( ToPort( pin ) );
				}
			}

			/// <summary>
			/// Determines whether the specified drive mode is supported for a GPIO pin.
			/// </summary>
			/// <remarks>Supported modes are <see cref="GpioPinDriveMode.Input"/>, <see
			/// cref="GpioPinDriveMode.InputPullUp"/>, and <see cref="GpioPinDriveMode.Output"/>.</remarks>
			/// <param name="pin">The GPIO pin number.</param>
			/// <param name="mode">The drive mode to evaluate.</param>
			/// <returns><see langword="true"/> if the drive mode is supported; otherwise, <see langword="false"/>.</returns>
			public bool IsDriveModeSupported( int pin, GpioPinDriveMode mode )
			{
				this.ValidatePin( pin );
				return mode == GpioPinDriveMode.Input ||
					   mode == GpioPinDriveMode.InputPullUp ||
					   mode == GpioPinDriveMode.Output;
			}

			/// <summary>
			/// Sets the value-changed callback and edge trigger for a pin.
			/// </summary>
			/// <remarks>Updates interrupt configuration for the port that contains the pin.</remarks>
			/// <param name="pin">The pin number to configure.</param>
			/// <param name="edge">The pin edge transition that triggers the callback.</param>
			/// <param name="value">The callback to invoke when the configured edge is detected.</param>
			public void SetPinChangedHandler( int pin, GpioPinEdge edge, GpioPinValueChangedEventHandler value )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					this.valueChangedEdges[ pin ] = edge;
					this.valueChangedHandlers[ pin ] = value;
					this.RefreshInterruptConfigurationForPort( this.ToPort( pin ) );
				}
			}

			/// <summary>
			/// Removes all value-changed event handling for the specified pin and updates the interrupt configuration for its
			/// port.
			/// </summary>
			/// <remarks>The pin is validated and must be open before the handler state is reset.</remarks>
			/// <param name="pin">The GPIO pin number whose value-changed handler and edge configuration are cleared.</param>
			public void ClearPinChangedHandler( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					EnsureNotDisposed();
					RequireOpenPin( pin );

					valueChangedEdges[ pin ] = 0;
					valueChangedHandlers[ pin ] = null!;
					RefreshInterruptConfigurationForPort( ToPort( pin ) );
				}
			}

			/// <summary>
			/// Sets the drive mode for an already opened GPIO pin on the MCP23017.
			/// </summary>
			/// <param name="pin">The logical pin number to configure.</param>
			/// <param name="mode">
			/// The drive mode to apply. Supported modes are <see cref="GpioPinDriveMode.Input"/>,
			/// <see cref="GpioPinDriveMode.InputPullUp"/>, and <see cref="GpioPinDriveMode.Output"/>.
			/// </param>
			/// <exception cref="ObjectDisposedException">Thrown when the controller has been disposed.</exception>
			/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="pin"/> is outside the valid range.</exception>
			/// <exception cref="InvalidOperationException">Thrown when <paramref name="pin"/> is not currently open.</exception>
			/// <exception cref="NotSupportedException">Thrown when <paramref name="mode"/> is not supported by the MCP23017.</exception>
			/// <remarks>
			/// This method updates both <c>IODIR</c> and <c>GPPU</c> register bits for the target pin:
			/// output clears direction and pull-up bits, input with pull-up sets both bits, and plain input
			/// sets direction while clearing pull-up. After writing registers, the input snapshot and interrupt
			/// configuration for the corresponding port are refreshed.
			/// </remarks>
			public void SetDriveMode( int pin, GpioPinDriveMode mode )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					if( !this.IsDriveModeSupported( pin, mode ) )
					{
						throw new NotSupportedException( $"The DriveMode {mode} is not supported by the MCP23017." );
					}

					this.driveModes[ pin ] = mode;

					Port port = this.ToPort( pin );
					byte mask = this.ToMask( pin );
					byte iodir = this.owner.ReadRegister( Register.IODIR, port );
					byte gppu = this.owner.ReadRegister( Register.GPPU, port );

					if( mode == GpioPinDriveMode.Output )
					{
						iodir &= ( byte )~mask;
						gppu &= ( byte )~mask;
					}
					else if( mode == GpioPinDriveMode.InputPullUp )
					{
						iodir |= mask;
						gppu |= mask;
					}
					else
					{
						iodir |= mask;
						gppu &= ( byte )~mask;
					}

					this.owner.WriteRegister( Register.IODIR, port, iodir );
					this.owner.WriteRegister( Register.GPPU, port, gppu );

					this.RefreshSnapshot( port );
					this.RefreshInterruptConfigurationForPort( port );
				}
			}

			/// <summary>
			/// Gets the drive mode configured for the specified GPIO pin.
			/// </summary>
			/// <param name="pin">The pin number to query.</param>
			/// <returns>The drive mode of the specified pin.</returns>
			public GpioPinDriveMode GetDriveMode( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );
					return this.driveModes[ pin ];
				}
			}

			/// <summary>
			/// Configures the direction of all pins on the specified port from an 8-bit mask and updates the cached pin modes
			/// and port state.
			/// </summary>
			/// <remarks>Writes the port direction register and disables pull-up resistors for the port, then refreshes
			/// the port snapshot and interrupt configuration.</remarks>
			/// <param name="portState">Bit mask that defines pin direction for the target port. A bit value of 1 sets the corresponding pin to output; 0
			/// sets it to input.</param>
			/// <param name="port">Port to configure.</param>
			public void SetPortMode( byte portState, Port port )
			{
				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					byte iodir = unchecked(( byte )~portState);

					this.owner.WriteRegister( Register.IODIR, port, iodir );
					this.owner.WriteRegister( Register.GPPU, port, 0x00 );

					for( int bit = 0; bit < 8; bit++ )
					{
						int pin = this.ToPinIndex( port, bit );
						bool output = ( portState & ( 1 << bit ) ) != 0;
						this.driveModes[ pin ] = output ? GpioPinDriveMode.Output : GpioPinDriveMode.Input;
					}

					this.RefreshSnapshot( port );
					this.RefreshInterruptConfigurationForPort( port );
				}
			}

			/// <summary>
			/// Configures all pins on the specified port from a bit mask and enables pull-ups on pins configured as inputs.
			/// </summary>
			/// <remarks>Returns without changing configuration when <paramref name="pu"/> is not <see
			/// cref="PinMode.InputPullUp"/>.</remarks>
			/// <param name="portState">Bit mask that defines the port pin states, where each bit represents one pin in the port.</param>
			/// <param name="port">Port to configure.</param>
			/// <param name="pu">Pin mode to apply. Only <see cref="PinMode.InputPullUp"/> is supported.</param>
			public void SetPortMode( byte portState, Port port, PinMode pu )
			{
				if( pu != PinMode.InputPullUp )
				{
					return;
				}

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					byte iodir = unchecked(( byte )~portState);
					byte gppu = iodir;

					this.owner.WriteRegister( Register.IODIR, port, iodir );
					this.owner.WriteRegister( Register.GPPU, port, gppu );

					for( int bit = 0; bit < 8; bit++ )
					{
						int pin = this.ToPinIndex( port, bit );
						bool output = ( portState & ( 1 << bit ) ) != 0;
						this.driveModes[ pin ] = output ? GpioPinDriveMode.Output : GpioPinDriveMode.InputPullUp;
					}

					this.RefreshSnapshot( port );
					this.RefreshInterruptConfigurationForPort( port );
				}
			}

			/// <summary>
			/// Reads the current value of a GPIO pin.
			/// </summary>
			/// <remarks>Requires a valid, open pin and reads the state from the underlying GPIO register.</remarks>
			/// <param name="pin">The pin number to read.</param>
			/// <returns>The current pin value as <see cref="GpioPinValue.High"/> or <see cref="GpioPinValue.Low"/>.</returns>
			public GpioPinValue Read( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					Port port = this.ToPort( pin );
					byte value = this.owner.ReadRegister( Register.GPIO, port );
					return ( ( value >> this.ToBitIndex( pin ) ) & 0x01 ) != 0 ? GpioPinValue.High : GpioPinValue.Low;
				}
			}

			/// <summary>
			/// Writes a logical value to an open pin configured for output.
			/// </summary>
			/// <remarks>Updates the output latch bit for the target pin and refreshes the cached port
			/// snapshot.</remarks>
			/// <param name="pin">The logical pin number to write.</param>
			/// <param name="value">The value to write to the pin.</param>
			/// <exception cref="InvalidOperationException">The pin is not configured for output mode.</exception>
			public void Write( int pin, GpioPinValue value )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					if( this.driveModes[ pin ] != GpioPinDriveMode.Output )
					{
						throw new InvalidOperationException( $"Pin {pin} is not configured as an output." );
					}

					Port port = this.ToPort( pin );
					byte mask = this.ToMask( pin );
					byte olat = this.owner.ReadRegister( Register.OLAT, port );

					if( value == GpioPinValue.High )
					{
						olat |= mask;
					}
					else
					{
						olat &= ( byte )~mask;
					}

					this.owner.WriteRegister( Register.OLAT, port, olat );
					this.RefreshSnapshot( port );
				}
			}

			/// <summary>
			/// Transfers a pin feature configuration between two pins.
			/// </summary>
			/// <remarks>This member is not supported for MCP23017 and always throws.</remarks>
			/// <param name="pinSource">Source pin number.</param>
			/// <param name="pinDestination">Destination pin number.</param>
			/// <param name="mode">Pin mode to transfer.</param>
			/// <param name="type">Pin type to transfer.</param>
			/// <param name="direction">Pin direction to transfer.</param>
			/// <param name="speed">Pin speed to transfer.</param>
			/// <param name="alternate">Alternate function to transfer.</param>
			/// <exception cref="NotSupportedException">The MCP23017 does not support alternate pin functions.</exception>
			public void TransferFeature( int pinSource, int pinDestination, uint mode, uint type, uint direction, uint speed, uint alternate )
			{
				throw new NotSupportedException( "The MCP23017 does not support alternate functions." );
			}

			/// <summary>
			/// Gets the debounce timeout configured for the specified pin.
			/// </summary>
			/// <param name="pin">The GPIO pin number.</param>
			/// <returns>The debounce timeout for the specified pin.</returns>
			public TimeSpan GetDebounceTimeout( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );
					return this.debounceTimeouts[ pin ];
				}
			}

			/// <summary>
			/// Sets the debounce timeout interval for a pin.
			/// </summary>
			/// <remarks>The pin must be valid and opened before the timeout is set.</remarks>
			/// <param name="pin">The GPIO pin number to configure.</param>
			/// <param name="value">The debounce interval to apply to the pin.</param>
			public void SetDebounceTimeout( int pin, TimeSpan value )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );
					this.debounceTimeouts[ pin ] = value;
				}
			}

			/// <summary>
			/// Releases resources, detaches the interrupt pin event handler, and marks the instance as disposed.
			/// </summary>
			/// <remarks>Safe to call multiple times. Uses synchronization to avoid concurrent disposal and sets the
			/// interrupt state to pending for waiting operations.</remarks>
			public void Dispose()
			{
				if( this.disposed )
				{
					return;
				}

				lock( this.syncRoot )
				{
					if( this.disposed )
					{
						return;
					}

					this.disposed = true;

					if( this.interruptPin != null )
					{
						this.interruptPin.ValueChanged -= this.OnInterruptPinValueChanged;
					}
				}

				lock( this.interruptGate )
				{
					this.interruptPending = true;
				}
			}

			/// <summary>
			/// Handles interrupt pin value changes by marking an interrupt as pending when a rising edge is detected.
			/// </summary>
			/// <remarks>Ignores non-rising edges and does not mark an interrupt after disposal. Access to interrupt
			/// state is synchronized.</remarks>
			/// <param name="sender">The source of the GPIO value-changed event.</param>
			/// <param name="e">Event data that indicates which GPIO edge triggered the event.</param>
			private void OnInterruptPinValueChanged( object sender, GpioPinValueChangedEventArgs e )
			{
				if( e.Edge != GpioPinEdge.RisingEdge )
				{
					return;
				}

				lock( this.interruptGate )
				{
					if( this.disposed )
					{
						return;
					}

					this.interruptPending = true;
				}
			}

			/// <summary>
			/// Runs the interrupt worker loop until disposal, processing pending interrupts when eligible.
			/// </summary>
			/// <remarks>Sleeps briefly when interrupts should not be processed or when an exception occurs to reduce
			/// CPU usage and keep the loop running.</remarks>
			private void InterruptWorkerLoop()
			{
				while( !this.disposed )
				{
					try
					{
						if( !this.ShouldProcessInterrupts() )
						{
							Thread.Sleep( 1 );
							continue;
						}

						this.ProcessInterrupts();
					}
					catch
					{
						Thread.Sleep( 1 );
					}
				}
			}

			/// <summary>
			/// Determines whether interrupt handling should proceed based on pending state or the interrupt signal level.
			/// </summary>
			/// <remarks>Checks the pending flag under a synchronization lock before reading the GPIO pin
			/// state.</remarks>
			/// <returns><see langword="true"/> when an interrupt is already marked as pending or when the interrupt pin is available and
			/// reads high; otherwise, <see langword="false"/>.</returns>
			private bool ShouldProcessInterrupts()
			{
				lock( this.interruptGate )
				{
					if( this.interruptPending )
					{
						return true;
					}
				}

				return this.interruptPin != null && this.interruptPin.Read() == GpioPinValue.High;
			}

			/// <summary>
			/// Processes pending and signaled interrupts until no additional work is available or the instance is disposed.
			/// </summary>
			/// <remarks>Checks queued interrupt state and the interrupt GPIO signal, services interrupts in a loop, and
			/// exits when the signal is inactive and no pending interrupts remain. Briefly waits before retrying when the signal
			/// remains active but no interrupt is serviced in the current iteration.</remarks>
			private void ProcessInterrupts()
			{
				while( !this.disposed )
				{
					bool hadPending = this.TakePendingInterrupt();
					if( !hadPending && ( this.interruptPin == null || this.interruptPin.Read() == GpioPinValue.Low ) )
					{
						return;
					}

					if( !this.ServiceInterruptsOnce() )
					{
						if( this.interruptPin == null || this.interruptPin.Read() == GpioPinValue.Low )
						{
							return;
						}

						Thread.Sleep( 1 );
					}
				}
			}

			/// <summary>
			/// Atomically checks for a pending interrupt and consumes it if present.
			/// </summary>
			/// <remarks>Synchronization is enforced with an internal lock to prevent race conditions when reading and
			/// clearing the pending state.</remarks>
			/// <returns>true if an interrupt was pending and was consumed; otherwise, false.</returns>
			private bool TakePendingInterrupt()
			{
				lock( this.interruptGate )
				{
					if( !this.interruptPending )
					{
						return false;
					}

					this.interruptPending = false;
					return true;
				}
			}

			/// <summary>
			/// Processes a single GPIO interrupt cycle for port A by reading interrupt state, collecting changed pins, and
			/// invoking queued value-changed handlers.
			/// </summary>
			/// <remarks>Uses a fallback comparison against the current port state when interrupt flags are already
			/// cleared due to timing, and updates the cached last-known port value.</remarks>
			/// <returns><see langword="true"/> when one or more pin changes are detected and dispatched; otherwise, <see
			/// langword="false"/>.</returns>
			private bool ServiceInterruptsOnce()
			{
				GpioPinValueChangedEventHandler[] handlers = new GpioPinValueChangedEventHandler[ PinCountValue ];
				GpioPinEdge[] edges = new GpioPinEdge[ PinCountValue ];
				int count = 0;

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					byte capturedA = this.owner.ReadRegister( Register.INTCAP, Port.A );
					byte currentA = this.owner.ReadRegister( Register.GPIO, Port.A );
					byte intfA = this.owner.ReadRegister( Register.INTF, Port.A );

					this.CollectChangedPinsFromInterruptStatus(
						Port.A,
						intfA,
						this.lastPortValues[ 0 ],
						capturedA,
						handlers,
						edges,
						ref count );

					if( count == 0 )
					{
						// Fallback, if INTF is already 0 in the timing case
						this.CollectChangedPinsFromInterruptStatus(
							Port.A,
							0xFF,
							this.lastPortValues[ 0 ],
							currentA,
							handlers,
							edges,
							ref count );
					}

					this.lastPortValues[ 0 ] = currentA;
				}

				for( int i = 0; i < count; i++ )
				{
					handlers[ i ]( null, new GpioPinValueChangedEventArgs( edges[ i ], DateTime.UtcNow ) );
				}

				return count > 0;
			}

			/// <summary>
			/// Collects value-changed handlers and detected edges for pins in the specified port that are flagged in the
			/// interrupt status and changed between the previous and captured states.
			/// </summary>
			/// <remarks>Only bits set in the interrupt flags are considered. A pin is collected only when its level
			/// changed and a handler is registered. Entries are appended in ascending bit order.</remarks>
			/// <param name="port">Identifies the port whose pin bits are evaluated.</param>
			/// <param name="intf">Interrupt flag bits indicating which pins have a pending interrupt condition.</param>
			/// <param name="previous">Previous sampled pin-state bits used as the baseline for edge detection.</param>
			/// <param name="captured">Captured pin-state bits used to determine the current level and edge direction.</param>
			/// <param name="handlers">Destination array that receives handlers for pins with detected changes and registered callbacks.</param>
			/// <param name="edges">Destination array that receives the edge corresponding to each collected handler.</param>
			/// <param name="count">Index into the destination arrays and running total of collected entries; incremented for each collected pin.</param>
			private void CollectChangedPinsFromInterruptStatus(
				Port port,
				byte intf,
				byte previous,
				byte captured,
				GpioPinValueChangedEventHandler[] handlers,
				GpioPinEdge[] edges,
				ref int count )
			{
				for( int bit = 0; bit < 8; bit++ )
				{
					byte mask = ( byte )( 1 << bit );

					if( ( intf & mask ) == 0 )
					{
						continue;
					}

					bool previousHigh = ( previous & mask ) != 0;
					bool capturedHigh = ( captured & mask ) != 0;

					if( previousHigh == capturedHigh )
					{
						continue;
					}

					int pin = this.ToPinIndex( port, bit );
					GpioPinValueChangedEventHandler handler = this.valueChangedHandlers[ pin ];
					if( handler == null )
					{
						continue;
					}

					GpioPinEdge actualEdge = capturedHigh ? GpioPinEdge.RisingEdge : GpioPinEdge.FallingEdge;

					handlers[ count ] = handler;
					edges[ count ] = actualEdge;
					count++;
				}
			}

			/// <summary>
			/// Updates interrupt-related register configuration for the specified port based on currently open pins, registered
			/// value-changed handlers, and input drive modes.
			/// </summary>
			/// <remarks>Enables per-bit interrupt-on-change only for pins that are open, have a value-changed handler,
			/// and are configured as input or input with pull-up. Writes the computed mask to GPINTEN and configures INTCON for
			/// comparison against previous pin state.</remarks>
			/// <param name="port">The port whose interrupt enable and interrupt control registers are refreshed.</param>
			private void RefreshInterruptConfigurationForPort( Port port )
			{
				byte gpinten = 0x00;
				byte intcon = 0x00;

				for( int bit = 0; bit < 8; bit++ )
				{
					int pin = this.ToPinIndex( port, bit );
					bool enabled =
						this.openPins[ pin ] &&
						this.valueChangedHandlers[ pin ] != null &&
						( this.driveModes[ pin ] == GpioPinDriveMode.Input || this.driveModes[ pin ] == GpioPinDriveMode.InputPullUp );

					if( enabled )
					{
						gpinten |= ( byte )( 1 << bit );
					}
				}

				this.owner.WriteRegister( Register.GPINTEN, port, gpinten );
				this.owner.WriteRegister( Register.INTCON, port, intcon );
			}

			/// <summary>
			/// Configures IOCON interrupt settings on both ports based on whether a shared interrupt pin is available.
			/// </summary>
			/// <remarks>When a shared interrupt pin is present, enables mirrored interrupts, sets interrupt polarity to
			/// active-high, and uses push-pull output mode. Otherwise, disables mirroring, clears active-high polarity, and
			/// keeps push-pull mode. Writes the updated IOCON values back to both port A and port B.</remarks>
			private void ConfigureInterruptHardware()
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( this.interruptPin != null )
				{
					// INT A als gemeinsame Interrupt-Leitung: active-high, push-pull, mirrored.
					ioConA |= IoConMirrorMask;
					ioConB |= IoConMirrorMask;

					ioConA |= IoConIntPolMask;
					ioConB |= IoConIntPolMask;

					ioConA &= unchecked(( byte )~IoConOdrMask);
					ioConB &= unchecked(( byte )~IoConOdrMask);
				}
				else
				{
					ioConA &= unchecked(( byte )~IoConMirrorMask);
					ioConB &= unchecked(( byte )~IoConMirrorMask);
					ioConA &= unchecked(( byte )~IoConIntPolMask);
					ioConB &= unchecked(( byte )~IoConIntPolMask);
					ioConA &= unchecked(( byte )~IoConOdrMask);
					ioConB &= unchecked(( byte )~IoConOdrMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			/// <summary>
			/// Updates the cached GPIO snapshot for the specified port by reading its current register value.
			/// </summary>
			/// <param name="port">Port to refresh in the snapshot cache.</param>
			private void RefreshSnapshot( Port port )
			{
				if( port == Port.A )
				{
					this.lastPortValues[ 0 ] = this.owner.ReadRegister( Register.GPIO, Port.A );
				}
				else
				{
					this.lastPortValues[ 1 ] = this.owner.ReadRegister( Register.GPIO, Port.B );
				}
			}

			/// <summary>
			/// Converts a port-relative bit position to a zero-based pin index.
			/// </summary>
			/// <param name="port">The port that contains the bit position.</param>
			/// <param name="bit">The zero-based bit position within the specified port.</param>
			/// <returns>The zero-based pin index, where <c>Port.A</c> maps to 0 through 7 and the other port maps to 8 through 15.</returns>
			private int ToPinIndex( Port port, int bit )
			{
				return ( port == Port.A ? 0 : 8 ) + bit;
			}

			/// <summary>
			/// Converts a pin number to its corresponding port.
			/// </summary>
			/// <param name="pin">Pin number to map to a port.</param>
			/// <returns>Port.A when the pin number is less than 8; otherwise, Port.B.</returns>
			private Port ToPort( int pin )
			{
				return pin < 8 ? Port.A : Port.B;
			}

			/// <summary>
			/// Converts a pin number to its zero-based bit index within an 8-bit group.
			/// </summary>
			/// <param name="pin">Pin number to convert.</param>
			/// <returns>Remainder of dividing <paramref name="pin"/> by 8.</returns>
			private int ToBitIndex( int pin )
			{
				return pin % 8;
			}

			/// <summary>
			/// Converts a pin number to a bit mask with the corresponding bit set.
			/// </summary>
			/// <param name="pin">Pin number to convert to a mask.</param>
			/// <returns>A byte value with a single bit set at the position mapped from the specified pin.</returns>
			private byte ToMask( int pin )
			{
				return ( byte )( 1 << this.ToBitIndex( pin ) );
			}

			/// <summary>
			/// Validates that the specified pin is currently open.
			/// </summary>
			/// <param name="pin">The pin number to validate.</param>
			/// <exception cref="InvalidOperationException">Thrown when the specified pin is not open.</exception>
			private void RequireOpenPin( int pin )
			{
				if( !this.openPins[ pin ] )
				{
					throw new InvalidOperationException( $"Pin {pin} is not opened." );
				}
			}

			/// <summary>
			/// Verifies that the current instance has not been disposed.
			/// </summary>
			/// <exception cref="ObjectDisposedException">The current instance has been disposed.</exception>
			private void EnsureNotDisposed()
			{
				if( this.disposed )
				{
					throw new ObjectDisposedException( nameof( Mcp23017 ) );
				}
			}

			/// <summary>
			/// Validates that a pin index is within the supported range.
			/// </summary>
			/// <param name="pin">The zero-based pin index to validate.</param>
			/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="pin"/> is less than 0 or greater than or equal to the maximum pin count.</exception>
			private void ValidatePin( int pin )
			{
				if( pin < 0 || pin >= PinCountValue )
				{
					throw new ArgumentOutOfRangeException( nameof( pin ), "The pin must be between 0 and 15." );
				}
			}

			/// <summary>
			/// Configures the interrupt output polarity by updating the INTPOL bit in the IOCON register on both ports.
			/// </summary>
			/// <remarks>Reads IOCON from ports A and B, updates the polarity bit, and writes the updated values back to
			/// both ports.</remarks>
			/// <param name="level">Polarity selector for the interrupt output; use 0 for active-low, any non-zero value for active-high.</param>
			public void SetInterruptPinPol( byte level )
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( level != 0 )
				{
					ioConA |= IoConIntPolMask;
					ioConB |= IoConIntPolMask;
				}
				else
				{
					ioConA &= unchecked(( byte )~IoConIntPolMask);
					ioConB &= unchecked(( byte )~IoConIntPolMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			/// <summary>
			/// Configures the interrupt output mode by setting or clearing the IOCON ODR bit on both ports.
			/// </summary>
			/// <remarks>Reads IOCON for ports A and B, updates the ODR flag, and writes the updated values back to both
			/// ports.</remarks>
			/// <param name="openDrain">Nonzero to enable open-drain interrupt outputs; 0 to disable open-drain mode.</param>
			public void SetIntOdr( byte openDrain )
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( openDrain != 0 )
				{
					ioConA |= IoConOdrMask;
					ioConB |= IoConOdrMask;
				}
				else
				{
					ioConA &= unchecked(( byte )~IoConOdrMask);
					ioConB &= unchecked(( byte )~IoConOdrMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			/// <summary>
			/// Configures whether interrupt outputs are mirrored so both interrupt pins reflect the same interrupt state.
			/// </summary>
			/// <remarks>Updates the IOCON MIRROR bit on both port A and port B configurations.</remarks>
			/// <param name="mirrored">Nonzero to enable interrupt mirroring; zero to disable it.</param>
			public void SetIntMirror( byte mirrored )
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( mirrored != 0 )
				{
					ioConA |= IoConMirrorMask;
					ioConB |= IoConMirrorMask;
				}
				else
				{
					ioConA &= unchecked(( byte )~IoConMirrorMask);
					ioConB &= unchecked(( byte )~IoConMirrorMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			/// <summary>
			/// Configures a GPIO pin to generate an interrupt when its input state changes.
			/// </summary>
			/// <remarks>Sets the pin direction to input, enables interrupt-on-change for the pin, and configures
			/// interrupt comparison against the previous pin value.</remarks>
			/// <param name="pin">Bit position of the pin within the selected port.</param>
			/// <param name="port">Port containing the pin to configure.</param>
			public void SetInterruptOnChangePin( byte pin, Port port )
			{
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte mask = ( byte )( 1 << pin );

				iodir |= mask;
				gpIntEn |= mask;
				intCon &= ( byte )~mask;

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
			}

			/// <summary>
			/// Configures a GPIO pin to generate an interrupt when its input value differs from the configured default level on
			/// the specified port.
			/// </summary>
			/// <remarks>Sets the pin direction to input, enables interrupt-on-change for the pin, and enables
			/// compare-against-DEFVAL interrupt mode.</remarks>
			/// <param name="pin">Zero-based pin index to configure.</param>
			/// <param name="port">GPIO port that contains the pin.</param>
			/// <param name="pinIntLevel">Default comparison level for the pin; 0 configures low and any nonzero value configures high.</param>
			public void SetInterruptOnDefValDevPin( byte pin, Port port, byte pinIntLevel )
			{
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );
				byte defVal = this.owner.ReadRegister( Register.DEFVAL, port );
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte mask = ( byte )( 1 << pin );

				iodir |= mask;
				gpIntEn |= mask;
				intCon |= mask;

				if( pinIntLevel != 0 )
				{
					defVal |= mask;
				}
				else
				{
					defVal &= ( byte )~mask;
				}

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
				this.owner.WriteRegister( Register.DEFVAL, port, defVal );
			}

			/// <summary>
			/// Configures selected pins on the specified port to generate interrupts when their input state changes.
			/// </summary>
			/// <remarks>Sets the selected pins as inputs, enables their interrupt-on-change bits, and configures
			/// comparison against the previous pin value by clearing the corresponding INTCON bits. Existing configuration of
			/// non-selected pins is preserved.</remarks>
			/// <param name="intOnChangePins">Bit mask of pins to configure for interrupt-on-change.</param>
			/// <param name="port">Port whose pins are configured.</param>
			public void SetInterruptOnChangePort( byte intOnChangePins, Port port )
			{
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );

				iodir |= intOnChangePins;
				gpIntEn |= intOnChangePins;
				intCon &= ( byte )~intOnChangePins;

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
			}

			/// <summary>
			/// Configures interrupt-on-change for selected pins on a device port using comparison against the default value
			/// register.
			/// </summary>
			/// <remarks>Sets selected pins as inputs, enables their interrupt-on-change, and configures interrupt
			/// control to compare pin state against DEFVAL.</remarks>
			/// <param name="intPins">Bit mask of pins to configure for interrupt-on-default-value behavior.</param>
			/// <param name="port">Port whose interrupt and direction registers are updated.</param>
			/// <param name="defVal">Default comparison value written to the port DEFVAL register.</param>
			public void SetInterruptOnDefValDevPort( byte intPins, Port port, byte defVal )
			{
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );

				iodir |= intPins;
				gpIntEn |= intPins;
				intCon |= intPins;

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
				this.owner.WriteRegister( Register.DEFVAL, port, defVal );
			}

			/// <summary>
			/// Disables all interrupt-on-change configuration for the specified port by clearing its interrupt enable and
			/// interrupt control settings.
			/// </summary>
			/// <param name="port">The port whose interrupt configuration is cleared.</param>
			public void DeleteAllInterruptsOnPort( Port port )
			{
				this.owner.WriteRegister( Register.GPINTEN, port, 0x00 );
				this.owner.WriteRegister( Register.INTCON, port, 0x00 );
			}

			/// <summary>
			/// Sets the input polarity configuration for a single pin on the specified port.
			/// </summary>
			/// <remarks>Performs a read-modify-write operation on the port IPOL register to update only the targeted
			/// pin.</remarks>
			/// <param name="pin">Zero-based pin index within the port.</param>
			/// <param name="port">Port containing the pin to configure.</param>
			/// <param name="ipolState">true to invert the pin input polarity; false to use normal polarity.</param>
			public void SetIpolPin( byte pin, Port port, bool ipolState )
			{
				byte ipolPortState = this.GetIpolPort( port );
				byte mask = ( byte )( 1 << pin );

				if( ipolState )
				{
					ipolPortState |= mask;
				}
				else
				{
					ipolPortState &= ( byte )~mask;
				}

				this.SetIpolPort( ipolPortState, port );
			}

			/// <summary>
			/// Sets the input polarity register value for the specified port.
			/// </summary>
			/// <param name="ipol">Bit mask that defines input polarity inversion for the selected port.</param>
			/// <param name="port">Port whose input polarity register is updated.</param>
			public void SetIpolPort( byte ipol, Port port )
			{
				this.owner.WriteRegister( Register.IPOL, port, ipol );
			}

			/// <summary>
			/// Gets the input polarity register value for the specified port.
			/// </summary>
			/// <param name="port">The port to read.</param>
			/// <returns>The input polarity register value for the specified port.</returns>
			public byte GetIpolPort( Port port )
			{
				return this.owner.ReadRegister( Register.IPOL, port );
			}

			/// <summary>
			/// Gets the interrupt flag register value for the specified port.
			/// </summary>
			/// <param name="port">The port whose interrupt flag value to read.</param>
			/// <returns>The interrupt flag register value for the specified port.</returns>
			public byte GetIntFlag( Port port )
			{
				return this.owner.ReadRegister( Register.INTF, port );
			}

			/// <summary>
			/// Gets the interrupt capture value from the INTCAP register for the specified port.
			/// </summary>
			/// <param name="port">The port whose INTCAP register is read.</param>
			/// <returns>The INTCAP register value for the specified port.</returns>
			public byte GetIntCap( Port port )
			{
				return this.owner.ReadRegister( Register.INTCAP, port );
			}
		}
	}
}
