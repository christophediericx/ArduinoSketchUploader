﻿using System;
using System.Threading;
using ArduinoUploader.Hardware;
using ArduinoUploader.Hardware.Memory;
using ArduinoUploader.Logging;
using ArduinoUploader.Protocols;
using ArduinoUploader.Protocols.STK500v1;
using ArduinoUploader.Protocols.STK500v1.Messages;

namespace ArduinoUploader.BootloaderProgrammers
{
    internal class OptibootBootloaderProgrammer : ArduinoBootloaderProgrammer
    {
        private static readonly ILog logger = LogProvider.For<OptibootBootloaderProgrammer>();

        internal OptibootBootloaderProgrammer(SerialPortConfig serialPortConfig, IMCU mcu)
            : base(serialPortConfig, mcu)
        {
        }

        public override void Open()
        {
            base.Open();
            // The Uno (and Nano R3) will have auto-reset because DTR is true when opening the serial connection, 
            // so we just wait a small amount of time for it to come back.
            Thread.Sleep(250);
        }

        protected override void Reset()
        {
            ToggleDtrRts(250, 50);
        }

        public override void EstablishSync()
        {
            int i;
            for (i = 0; i < MaxSyncRetries; i++)
            {
                Send(new GetSyncRequest());
                var result = Receive<GetSyncResponse>();
                if (result == null) continue;
                if (result.IsInSync) break;
            }

            if (i == MaxSyncRetries)
                UploaderLogger.LogErrorAndThrow(
                    string.Format(
                        "Unable to establish sync after {0} retries.", MaxSyncRetries));

            var nextByte = ReceiveNext();

            if (nextByte != Constants.RESP_STK_OK)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to establish sync.");
        }

        protected void SendWithSyncRetry(IRequest request)
        {
            byte nextByte;
            while (true)
            {
                Send(request);
                nextByte = (byte) ReceiveNext();
                if (nextByte == Constants.RESP_STK_NOSYNC)
                {
                    EstablishSync();
                    continue;
                }
                break;
            }
            if (nextByte != Constants.RESP_STK_INSYNC)
                UploaderLogger.LogErrorAndThrow(
                    string.Format(
                        "Unable to aqcuire sync in SendWithSyncRetry for request of type {0}!", 
                        request.GetType()));
        }

        public override void CheckDeviceSignature()
        {
            logger.Debug("Expecting to find '{0}'...", MCU.DeviceSignature);
            SendWithSyncRetry(new ReadSignatureRequest());
            var response = Receive<ReadSignatureResponse>(4);
            if (response == null || !response.IsCorrectResponse)
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
            var majorVersion = GetParameterValue(Constants.PARM_STK_SW_MAJOR);
            var minorVersion = GetParameterValue(Constants.PARM_STK_SW_MINOR);
            logger.Info("Retrieved software version: {0}.", 
                string.Format("{0}.{1}", majorVersion, minorVersion));

            logger.Info("Setting device programming parameters...");
            SendWithSyncRetry(new SetDeviceProgrammingParametersRequest((MCU)MCU));
            var nextByte = ReceiveNext();

            if (nextByte != Constants.RESP_STK_OK)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to set device programming parameters!");
        }

        public override void EnableProgrammingMode()
        {
            SendWithSyncRetry(new EnableProgrammingModeRequest());
            var nextByte = ReceiveNext();
            if (nextByte == Constants.RESP_STK_OK) return;
            if (nextByte == Constants.RESP_STK_NODEVICE || nextByte == Constants.RESP_STK_Failed)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to enable programming mode on the device!");
        }

        public override void LeaveProgrammingMode()
        {
            SendWithSyncRetry(new LeaveProgrammingModeRequest());
            var nextByte = ReceiveNext();
            if (nextByte == Constants.RESP_STK_OK) return;
            if (nextByte == Constants.RESP_STK_NODEVICE || nextByte == Constants.RESP_STK_Failed)
                UploaderLogger.LogErrorAndThrow(
                    "Unable to leave programming mode on the device!");
        }

        private uint GetParameterValue(byte param)
        {
            logger.Trace("Retrieving parameter '{0}'...", param);
            SendWithSyncRetry(new GetParameterRequest(param));
            var nextByte = ReceiveNext();
            var paramValue = (uint)nextByte;
            nextByte = ReceiveNext();

            if (nextByte == Constants.RESP_STK_Failed)
                UploaderLogger.LogErrorAndThrow(
                    string.Format("Retrieving parameter '{0}' failed!", param));

            if (nextByte != Constants.RESP_STK_OK)
                UploaderLogger.LogErrorAndThrow(
                    string.Format(
                        "General protocol error while retrieving parameter '{0}'.", 
                        param));

            return paramValue;
        }

        public override void ExecuteWritePage(IMemory memory, int offset, byte[] bytes)
        {
            SendWithSyncRetry(new ExecuteProgramPageRequest(memory, bytes));
            var nextByte = ReceiveNext();
            if (nextByte == Constants.RESP_STK_OK) return;
            UploaderLogger.LogErrorAndThrow(
                string.Format("Write at offset {0} failed!", offset));
        }

        public override byte[] ExecuteReadPage(IMemory memory)
        {
            var pageSize = memory.PageSize;
            SendWithSyncRetry(new ExecuteReadPageRequest(memory.Type, pageSize));
            var bytes = ReceiveNext(pageSize);
            if (bytes == null)
                UploaderLogger.LogErrorAndThrow("Execute read page failed!");                

            var nextByte = ReceiveNext();
            if (nextByte == Constants.RESP_STK_OK) return bytes;
            UploaderLogger.LogErrorAndThrow("Execute read page failed!");
            return null;
        }

        public override void LoadAddress(IMemory memory, int addr)
        {
            logger.Trace("Sending load address request: {0}.", addr);
            addr = addr >> 1;
            SendWithSyncRetry(new LoadAddressRequest(addr));
            var result = ReceiveNext();
            if (result == Constants.RESP_STK_OK) return;
            UploaderLogger.LogErrorAndThrow(string.Format("LoadAddress failed with result {0}!", result));
        }
    }
}
