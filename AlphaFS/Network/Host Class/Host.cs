/*  Copyright (C) 2008-2015 Peter Palotas, Jeffrey Jangli, Alexandr Normuradov
 *  
 *  Permission is hereby granted, free of charge, to any person obtaining a copy 
 *  of this software and associated documentation files (the "Software"), to deal 
 *  in the Software without restriction, including without limitation the rights 
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 *  copies of the Software, and to permit persons to whom the Software is 
 *  furnished to do so, subject to the following conditions:
 *  
 *  The above copyright notice and this permission notice shall be included in 
 *  all copies or substantial portions of the Software.
 *  
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN 
 *  THE SOFTWARE. 
 */

using Alphaleonis.Win32.Filesystem;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Alphaleonis.Win32.Network
{
   /// <summary>Provides static methods to retrieve network resource information from a local- or remote host.</summary>
   public static partial class Host
   {
      #region GetUncName

      /// <summary>Return the host name in UNC format, for example: \\hostname.</summary>
      /// <returns>The unc name.</returns>
      [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
      [SecurityCritical]
      public static string GetUncName()
      {
         return string.Format(CultureInfo.CurrentCulture, "{0}{1}", Path.UncPrefix, Environment.MachineName);
      }

      /// <summary>Return the host name in UNC format, for example: \\hostname.</summary>
      /// <param name="computerName">Name of the computer.</param>
      /// <returns>The unc name.</returns>
      [SuppressMessage("Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "0", Justification = "Utils.IsNullOrWhiteSpace validates arguments.")]
      [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
      [SecurityCritical]
      public static string GetUncName(string computerName)
      {
         return Utils.IsNullOrWhiteSpace(computerName)
            ? GetUncName()
            : (computerName.StartsWith(Path.UncPrefix, StringComparison.OrdinalIgnoreCase)
               ? computerName.Trim()
               : Path.UncPrefix + computerName.Trim());
      }

      #endregion // GetUncName
      

      #region Internal

      #region EnumerateNetworkObjectInternal

      private delegate uint EnumerateNetworkObjectDelegate(
         FunctionData functionData, out IntPtr netApiBuffer, [MarshalAs(UnmanagedType.I4)] int prefMaxLen,
         [MarshalAs(UnmanagedType.U4)] out uint entriesRead, [MarshalAs(UnmanagedType.U4)] out uint totalEntries,
         [MarshalAs(UnmanagedType.U4)] out uint resumeHandle);

      /// <summary>Structure is used to pass additional data to the Win32 function.</summary>
      private struct FunctionData
      {
         public int EnumType;
         public string ExtraData1;
         public string ExtraData2;
      }

      [SecurityCritical]
      private static IEnumerable<TStruct> EnumerateNetworkObjectInternal<TStruct, TNative>(FunctionData functionData, Func<TNative, IntPtr, TStruct> createTStruct, EnumerateNetworkObjectDelegate enumerateNetworkObject, bool continueOnException)
      {
         Type objectType;
         int objectSize;
         bool isString;

         switch (functionData.EnumType)
         {
            // Logical Drives
            case 1:
               objectType = typeof(IntPtr);
               isString = true;
               objectSize = Marshal.SizeOf(objectType) + UnicodeEncoding.CharSize;
               break;

            default:
               objectType = typeof(TNative);
               isString = objectType == typeof(string);
               objectSize = isString ? 0 : Marshal.SizeOf(objectType);
               break;
         }


         var buffer = IntPtr.Zero;

         try
         {
            uint lastError;
            do
            {
               uint entriesRead;
               uint totalEntries;
               uint resumeHandle;

               lastError = enumerateNetworkObject(functionData, out buffer, NativeMethods.MaxPreferredLength, out entriesRead, out totalEntries, out resumeHandle);

               switch (lastError)
               {
                  case Win32Errors.NERR_Success:
                  case Win32Errors.ERROR_MORE_DATA:
                     if (entriesRead > 0)
                     {
                        for (long i = 0, itemOffset = buffer.ToInt64(); i < entriesRead; i++, itemOffset += objectSize)
                           yield return (TStruct) (isString
                              ? Marshal.PtrToStringUni(new IntPtr(itemOffset))
                              : (object) createTStruct((TNative) Marshal.PtrToStructure(new IntPtr(itemOffset), objectType), buffer));
                     }
                     break;

                  case Win32Errors.ERROR_BAD_NETPATH:
                     break;

                  // Observed when ShareInfo503 is requested, but not supported/possible.
                  case Win32Errors.RPC_X_BAD_STUB_DATA:
                     yield break;
               }

            } while (lastError == Win32Errors.ERROR_MORE_DATA);

            if (lastError != Win32Errors.NO_ERROR && !continueOnException)
               throw new NetworkInformationException((int) lastError);
         }
         finally
         {
            if (buffer != IntPtr.Zero)
               NativeMethods.NetApiBufferFree(buffer);
         }
      }

      #endregion // EnumerateNetworkObjectInternal
      
      #region GetRemoteNameInfoInternal

      /// <summary>This method uses <see cref="NativeMethods.RemoteNameInfo"/> level to retieve full REMOTE_NAME_INFO structure.</summary>
      /// <returns>A <see cref="NativeMethods.RemoteNameInfo"/> structure.</returns>
      /// <remarks>AlphaFS regards network drives created using SUBST.EXE as invalid.</remarks>
      /// <exception cref="ArgumentException">The path parameter contains invalid characters, is empty, or contains only white spaces.</exception>
      /// <exception cref="ArgumentNullException"/>
      /// <exception cref="System.IO.PathTooLongException">When <paramref name="path"/> exceeds maximum path length.</exception>
      /// <exception cref="NetworkInformationException"></exception>
      /// <param name="path">The local path with drive name.</param>
      /// <param name="continueOnException"><see langword="true"/> suppress any Exception that might be thrown a result from a failure, such as unavailable resources.</param>
      [SecurityCritical]
      internal static NativeMethods.RemoteNameInfo GetRemoteNameInfoInternal(string path, bool continueOnException)
      {
         if (Utils.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException("path");

         path = Path.GetRegularPathInternal(path, GetFullPathOptions.CheckInvalidPathChars); 

         // If path already is a network share path, we fill the RemoteNameInfo structure ourselves.
         if (Path.IsUncPath(path, false))
            return new NativeMethods.RemoteNameInfo
            {
               UniversalName = Path.AddTrailingDirectorySeparator(path, false),
               ConnectionName = Path.RemoveTrailingDirectorySeparator(path, false),
               RemainingPath = Path.DirectorySeparator
            };


         // Use large enough buffer to prevent a 2nd call.
         uint bufferSize = 1024;
         var buffer = new IntPtr(bufferSize);

         try
         {
            uint lastError;
            do
            {
               // Allocate the memory.
               buffer = Marshal.AllocHGlobal((int) bufferSize);

               // Structure: UNIVERSAL_NAME_INFO_LEVEL = 1 (not used in AlphaFS).
               // Structure: REMOTE_NAME_INFO_LEVEL    = 2

               lastError = NativeMethods.WNetGetUniversalName(path, 2, buffer, out bufferSize);

               switch (lastError)
               {
                  case Win32Errors.NO_ERROR:
                     return Utils.MarshalPtrToStructure<NativeMethods.RemoteNameInfo>(0, buffer);

                  case Win32Errors.ERROR_MORE_DATA:
                     //bufferSize = Received the required buffer size, retry.

                     if (buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(buffer);
                     break;
               }

            } while (lastError == Win32Errors.ERROR_MORE_DATA);

            if (!continueOnException && lastError != Win32Errors.NO_ERROR)
               throw new NetworkInformationException((int) lastError);

            // Return an empty structure (all fields set to null).
            return new NativeMethods.RemoteNameInfo();
         }
         finally
         {
            if (buffer != IntPtr.Zero)
               Marshal.FreeHGlobal(buffer);
         }
      }

      #endregion // GetRemoteNameInfoInternal

      internal struct ConnectDisconnectArguments
      {
         /// <summary>Handle to a window that the provider of network resources can use as an owner window for dialog boxes.</summary>
         public IntPtr WinOwner;

         /// <summary>The name of a local device to be redirected, such as "F:". When <see cref="LocalName"/> is <see langword="null"/> or <c>string.Empty</c>, the last available drive letter will be used. Letters are assigned beginning with Z:, then Y: and so on.</summary>
         public string LocalName;

         /// <summary>A network resource to connect to/disconnect from, for example: \\server or \\server\share</summary>
         public string RemoteName;

         /// <summary>A <see cref="NetworkCredential"/> instance. Use either this or the combination of <see cref="UserName"/> and <see cref="Password"/>.</summary>
         public NetworkCredential Credential;

         /// <summary>The user name for making the connection. If <see cref="UserName"/> is <see langword="null"/>, the function uses the default user name. (The user context for the process provides the default user name)</summary>
         public string UserName;

         /// <summary>The password to be used for making the network connection. If <see cref="Password"/> is <see langword="null"/>, the function uses the current default password associated with the user specified by <see cref="UserName"/>.</summary>
         public string Password;

         /// <summary><see langword="true"/> always pop-ups an authentication dialog box.</summary>
         public bool Prompt;

         /// <summary><see langword="true"/> successful network resource connections will be saved.</summary>
         public bool UpdateProfile;

         /// <summary>When the operating system prompts for a credential, the credential should be saved by the credential manager when true.</summary>
         public bool SaveCredentials;

         /// <summary><see langword="true"/> indicates that the operation concerns a drive mapping.</summary>
         public bool IsDeviceMap;

         /// <summary><see langword="true"/> indicates that the operation needs to disconnect from the network resource, otherwise connect.</summary>
         public bool IsDisconnect;
      }

      #endregion // Internal
   }
}