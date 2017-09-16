﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ArduinoUploader.Hardware;
using ArduinoUploader.Hardware.Memory;
using ArduinoUploader.Logging;
using ArduinoUploader.Protocols.AVR109;
using ArduinoUploader.Protocols.AVR109.Messages;
using RJCP.IO.Ports;

namespace ArduinoUploader.BootloaderProgrammers
{
    internal class ButterflyBootloaderProgrammer : ArduinoBootloaderProgrammer
    {
        private static readonly ILog logger = LogProvider.For<ButterflyBootloaderProgrammer>();
        private const int VIRTUAL_COM_CREATION_TIMEOUT = 1000;
        private string[] originalPorts;

        public ButterflyBootloaderProgrammer(SerialPortConfig serialPortConfig, IMCU mcu)
            : base(serialPortConfig, mcu)
        {
        }

        protected override void Reset()
        {
            logger.Info("Issuing forced 1200bps reset...");
            var currentPortName = SerialPort.PortName;
            originalPorts = SerialPortStream.GetPortNames();

            SerialPort.Close();

            SerialPort = new SerialPortStream(currentPortName, 1200);
            SerialPort.Open();
            SerialPort.Close();
            Thread.Sleep(VIRTUAL_COM_CREATION_TIMEOUT);
        }

        public override void Close()
        {
            try
            {
                logger.Info("Closing...");
                SerialPort.Close();
                logger.Info("Waiting for virtual port to disappear...");
                Thread.Sleep(VIRTUAL_COM_CREATION_TIMEOUT);
            }
            catch (Exception ex)
            {
                UploaderLogger.LogErrorAndThrow(
                    string.Format("Exception during close of the programmer: '{0}'.", 
                    ex.Message));
            }
        }

        public override void EstablishSync()
        {
            var ports = SerialPortStream.GetPortNames();
            var newPort = ports.Except(originalPorts).SingleOrDefault();

            if (newPort == null)
                UploaderLogger.LogErrorAndThrow(
                    string.Format(
                        "No (unambiguous) virtual COM port detected (after {0}ms).",
                        VIRTUAL_COM_CREATION_TIMEOUT));

            SerialPort = new SerialPortStream
            {
                BaudRate = 57600,
                PortName = newPort,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.DtrRts
            };
            try
            {
                SerialPort.Open();
            }
            catch (Exception ex)
            {
                UploaderLogger.LogErrorAndThrow(
                    string.Format("Unable to open serial port - {0}.", ex.Message));
            }
        }

        public override void CheckDeviceSignature()
        {
            logger.Debug("Expecting to find '{0}'...", MCU.DeviceSignature);
            Send(new ReadSignatureBytesRequest());
            var response = Receive<ReadSignatureBytesResponse>(3);
            if (response == null)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to check device signature!");

            var signature = response.Signature;
            if (BitConverter.ToString(signature) != MCU.DeviceSignature)
                UploaderLogger.LogErrorAndThrow(
                    string.Format(
                        "Unexpected device signature - found '{0}'- expected '{1}'.",
                        BitConverter.ToString(signature),
                        MCU.DeviceSignature));
        }

        public override void InitializeDevice()
        {
            Send(new ReturnSoftwareIdentifierRequest());
            var softIdResponse = Receive<ReturnSoftwareIdentifierResponse>(7);
            if (softIdResponse == null)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to retrieve software identifier!");

            logger.Info("Software identifier: '{0}'",
                Encoding.ASCII.GetString(softIdResponse.Bytes));

            Send(new ReturnSoftwareVersionRequest());
            var softVersionResponse = Receive<ReturnSoftwareVersionResponse>(2);
            if (softVersionResponse == null)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to retrieve software version!");

            logger.Info("Software Version: {0}.{1}",
                softVersionResponse.MajorVersion, softVersionResponse.MinorVersion);

            Send(new ReturnProgrammerTypeRequest());
            var progTypeResponse = Receive<ReturnProgrammerTypeResponse>(1);
            if (progTypeResponse == null)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to retrieve programmer type!");

            logger.Info("Programmer type: {0}.", progTypeResponse.ProgrammerType);

            Send(new CheckBlockSupportRequest());
            var checkBlockResponse = Receive<CheckBlockSupportResponse>(3);
            if (checkBlockResponse == null) 
                UploaderLogger.LogErrorAndThrow("Unable to retrieve block support!");
            if (!checkBlockResponse.HasBlockSupport)
                UploaderLogger.LogErrorAndThrow("Block support is not supported!");

            logger.Info("Block support - buffer size {0} bytes.", checkBlockResponse.BufferSize);

            Send(new ReturnSupportedDeviceCodesRequest());
            var devices = new List<byte>();
            do
            {
                var nextByte = (byte) ReceiveNext();
                if (nextByte != Constants.NULL) devices.Add(nextByte);
                else break;
            } 
            while (true);

            var supportedDevices = string.Join("-", devices);
            logger.Info("Supported devices: {0}.", supportedDevices);

            var devCode = MCU.DeviceCode;
            if (!devices.Contains(devCode))
                UploaderLogger.LogErrorAndThrow(
                    string.Format("Device {0} not in supported list of devices: {1}!",
                    devCode, supportedDevices));

            logger.Info("Selecting device type '{0}'...", devCode);
            Send(new SelectDeviceTypeRequest(devCode));
            var response = ReceiveNext();
            if (response != Constants.CARRIAGE_RETURN)
                UploaderLogger.LogErrorAndThrow("Unable to execute select device type command!");
        }

        public override void EnableProgrammingMode()
        {
            Send(new EnterProgrammingModeRequest());
            var response = ReceiveNext();
            if (response != Constants.CARRIAGE_RETURN)
                UploaderLogger.LogErrorAndThrow("Unable to enter programming mode!");
        }

        public override void LoadAddress(IMemory memory, int offset)
        {
            logger.Trace("Sending load address request: {0}.", offset);
            Send(new SetAddressRequest(offset / 2));
            var response = ReceiveNext();
            if (response != Constants.CARRIAGE_RETURN)
                UploaderLogger.LogErrorAndThrow("Unable to execute set address request!");
        }

        public override byte[] ExecuteReadPage(IMemory memory)
        {
            var type = memory.Type;
            var blockSize = memory.PageSize;
            Send(new StartBlockReadRequest(type, blockSize));
            var response = Receive<StartBlockReadResponse>(blockSize);
            return response.Bytes;
        }

        public override void ExecuteWritePage(IMemory memory, int offset, byte[] bytes)
        {
            var type = memory.Type;
            var blockSize = memory.PageSize;
            Send(new StartBlockLoadRequest(type, blockSize, bytes));
            var response = ReceiveNext();
            if (response != Constants.CARRIAGE_RETURN)
                UploaderLogger.LogErrorAndThrow("Unable to execute write page!");
        }

        public override void LeaveProgrammingMode()
        {
            Send(new LeaveProgrammingModeRequest());
            var leaveProgModeResp = ReceiveNext();
            if (leaveProgModeResp != Constants.CARRIAGE_RETURN)
                UploaderLogger.LogErrorAndThrow("Unable to leave programming mode!");

            Send(new ExitBootLoaderRequest());
            var exitBootloaderResp = ReceiveNext();
            if (exitBootloaderResp != Constants.CARRIAGE_RETURN)
                UploaderLogger.LogErrorAndThrow("Unable to exit boot loader!");
        }
    }
}
