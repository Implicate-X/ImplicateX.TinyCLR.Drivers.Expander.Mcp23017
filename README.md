# ImplicateX.TinyCLR.Drivers.Expander.Mcp23017

🔌`TinyCLR` driver for the `MCP23017/MCP23S17` 16-bit GPIO expander.

Easily add sixteen additional logical input/output pins using a single I²C connection.

## 🎯 Overview

The MCP23017 is a 16-bit GPIO expander from Microchip that provides two independent 8-bit ports over an I²C bus.

The driver exposes the device as a collection of logical pins, allowing applications to work with additional inputs and outputs just like onboard GPIOs.

[!NOTE] The driver transforms a remote I²C or SPI peripheral into a local GPIO abstraction.

Applications operate on pins. The driver operates on registers.

## ✨ Features
✅ 16 GPIO pins<br/>
✅ I²C interface `MCP23017`<br/>
✅ SPI interface `MCP23S17`<br/>
✅ Individual pin direction control<br/>
✅ Input mode support<br/>
✅ Output mode support<br/>
✅ Internal pull-up/pull-down resistor support<br/>
✅ Port A and Port B access<br/>
✅ Multiple devices on the same bus<br/>
✅ TinyCLR OS optimized<br/>
✅ Low memory footprint<br/>
✅ Per-pin and per-port mode configuration<br/>
✅ Optional hardware reset pin<br/>
✅ Optional interrupt pin integration


## 🏗 Solution layout

- `ImplicateX.TinyCLR.Drivers.Expander.Mcp23017`  
  Driver library project.
- `App/ImplicateX.TinyCLR.Drivers.Expander.Mcp23017.App`  
  Sample TinyCLR application showing initialization and pin usage.

## 📝 Requirements

- .NET Framework `4.8`
- TinyCLR OS SDK / libraries (`GHIElectronics.TinyCLR.*` packages)
- Supported board (example in sample uses FEZ family pin mappings)

## 🚀 Quick start (I2C)
```csharp
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.I2c;
using ImplicateX.TinyCLR.Drivers.Expander;

// Create I2C device (bus/controller setup depends on your board)
var i2cSettings = new I2cConnectionSettings(0x20, I2cMode.Master, I2cAddressFormat.SevenBit, 100_000U); 
var i2cDevice = CreateI2cDevice(i2cController, i2cSettings);
// Optional interrupt pin from MCU to MCP23017 INTA/INTB 

var intPin = gpioController.OpenPin(controllerIntPin); 
intPin.SetDriveMode(GpioPinDriveMode.InputPullUp);

// Initialize driver 
var mcp = new Mcp23017(i2cDevice, interruptPin: intPin);

// Use expanded GPIO pins 
var led = mcp.OpenPin(15); 
led.SetDriveMode(GpioPinDriveMode.Output); 
led.Write(GpioPinValue.High);
```

## 📝 Notes

- Default I2C device address is `0x20` unless configured otherwise in hardware.
- See `App/Program.cs` for a complete initialization.

## ⚙️ Design Philosophy

The driver isolates register-level communication behind a simple and strongly typed API.

Applications should work with logical inputs and outputs rather than device registers, bit masks, and I²C transactions.

This results in:

simpler application code
improved readability
easier maintenance
reusable hardware abstractions

## 🔬 Typical Applications
📻 Radio projects<br/>
🎛 Front panel controllers<br/>
💡 LED indicators<br/>
⌨ Key matrices<br/>
📟 Displays<br/>
🔘 Push buttons<br/>
🏠 Home automation<br/>
🧪 Laboratory equipment

## 🏗 Part of the ImplicateX TinyCLR Driver Ecosystem

The MCP23017 driver is maintained as an independent package and can be used in any TinyCLR project.

Within larger applications such as WebTuner, the driver may also be referenced directly as a solution project, enabling source-level debugging and seamless integration during development.

## ❤️ TinyCLR OS

Designed for modern embedded development using .NET and TinyCLR OS.