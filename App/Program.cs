using System;
using GHIElectronics.TinyCLR.Pins;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.I2c;
using System.Threading;

namespace ImplicateX.TinyCLR.Drivers.Expander
{
	internal class Program
	{
		public static class ControllerPin
		{
			public const int V1INTA = FEZFeather.GpioPin.PD0;
			public const int V1Reset = FEZFeather.GpioPin.PD1;
		}

		public static class ExpanderPin
		{
			public const int KeyMenu = 0;
			public const int KeyAutopreset = 1;
			public const int KeyBand = 2;
			public const int KeyDimmer = 3;
			public const int LedRDS = 13;
			public const int LedTuned = 14;
			public const int LedStereo = 15;
		}

		private static GpioController gpioController = null!;
		private static I2cController i2cController = null!;
		private static Mcp23017 mcp23017 = null!;
		private static GpioPin ledStereoPin = null!;
		private static GpioPin ledTunedPin = null!;
		private static GpioPin ledRdsPin = null!;

		static void Main()
		{
			gpioController = GpioController.GetDefault();
			i2cController = I2cController.FromName( FEZDuino.I2cBus.I2c1 );

			#region IO Expander MCP23017 Reset sequence
			GpioPin v1ResetPin = gpioController.OpenPin( ControllerPin.V1Reset );
			v1ResetPin.SetDriveMode( GpioPinDriveMode.Output );
			v1ResetPin.Write( GpioPinValue.High );
			Thread.Sleep( 100 );
			v1ResetPin.Write( GpioPinValue.Low );
			Thread.Sleep( 100 );
			v1ResetPin.Write( GpioPinValue.High );
			Thread.Sleep( 100 );
			#endregion

			#region IO Expander MCP23017 initialization
			var mcpDevice = CreateI2cDevice(
				i2cController,
				new I2cConnectionSettings( 0x20, I2cMode.Master, I2cAddressFormat.SevenBit, 100_000U ) );

			/// Interrupt pin for MCP23017 Port A is connected to V1INTA (PD0) on the controller
			var intPin = gpioController.OpenPin( ControllerPin.V1INTA );
			intPin.SetDriveMode( GpioPinDriveMode.InputPullUp );

			mcp23017 = new Mcp23017( mcpDevice, interruptPin: intPin );
			#endregion

			#region Front panel keys pins
			GpioPin keyMenuPin = mcp23017.OpenPin( ExpanderPin.KeyMenu );
			keyMenuPin.SetDriveMode( GpioPinDriveMode.InputPullUp );
			GpioPin keyBandPin = mcp23017.OpenPin( ExpanderPin.KeyBand );
			keyBandPin.SetDriveMode( GpioPinDriveMode.InputPullUp );
			GpioPin keyAutopresetPin = mcp23017.OpenPin( ExpanderPin.KeyAutopreset );
			keyAutopresetPin.SetDriveMode( GpioPinDriveMode.InputPullUp );
			GpioPin keyDimmerPin = mcp23017.OpenPin( ExpanderPin.KeyDimmer );
			keyDimmerPin.SetDriveMode( GpioPinDriveMode.InputPullUp );
			#endregion

			#region Front panel LEDs pins
			ledTunedPin = mcp23017.OpenPin( ExpanderPin.LedTuned );
			ledTunedPin.SetDriveMode( GpioPinDriveMode.Output );
			ledTunedPin.Write( GpioPinValue.High );
			ledStereoPin = mcp23017.OpenPin( ExpanderPin.LedStereo );
			ledStereoPin.SetDriveMode( GpioPinDriveMode.Output );
			ledStereoPin.Write( GpioPinValue.High );
			ledRdsPin = mcp23017.OpenPin( ExpanderPin.LedRDS );
			ledRdsPin.SetDriveMode( GpioPinDriveMode.Output );
			ledRdsPin.Write( GpioPinValue.High );
			#endregion
		}

		private static I2cDevice CreateI2cDevice( I2cController controller, I2cConnectionSettings settings )
		{
			var type = controller.GetType();
			var method =
				type.GetMethod( "CreateI2cDevice", new[] { typeof( I2cConnectionSettings ) } ) ??
				type.GetMethod( "GetDevice", new[] { typeof( I2cConnectionSettings ) } );

			if( method == null )
			{
				throw new NotSupportedException( "No compatible method found to create an I2C device." );
			}

			return ( I2cDevice )method.Invoke( controller, new object[] { settings } );
		}
	}
}
